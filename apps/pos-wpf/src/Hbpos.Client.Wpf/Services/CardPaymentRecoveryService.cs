using System.IO;
using System.Text.Json;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using static Hbpos.Contracts.Linkly.LinklyCloudBackendStatusConstants;

namespace Hbpos.Client.Wpf.Services;

public enum CardPaymentRecoveryOutcome
{
    None,
    Checking,
    OrderCompleted,
    DraftRestored,
    Unknown,
    ActiveSessionApproved,
    ActiveSessionNotPaid,
    ActiveSessionManuallyCleared
}

public enum CardRefundSupervisorDecision
{
    ConfirmRefunded,
    ConfirmNotRefunded,
    ContinueWaiting
}

public sealed record CardRefundRecoveryDetails(
    Guid AttemptGuid,
    CardProcessorKind Processor,
    Guid? OperationGuid,
    decimal Amount,
    string? OriginalReference);

public sealed record CardRefundSupervisorResolution(
    Guid AttemptGuid,
    CardProcessorKind Processor,
    CardRefundSupervisorDecision Decision,
    string Reason,
    string? Evidence = null,
    string? RefundReference = null);

public sealed record CardRefundSupervisorResolutionResult(
    bool Succeeded,
    string Message,
    CardPaymentRecoveryResult? RecoveryResult = null,
    bool RetryAllowed = false,
    bool LockRetained = false,
    bool ResolutionPersisted = false,
    bool ResolutionApplied = false);

public sealed record CardRefundAttemptResolution(
    Guid AttemptGuid,
    CardRefundSupervisorDecision Decision,
    string Reason,
    string? Evidence,
    string? RefundReference,
    string? RetryTxnRef,
    DateTimeOffset ResolvedAt);

public enum CardPaymentSupervisorDecision
{
    ConfirmPaid,
    ConfirmNotPaid,
    ContinueWaiting
}

public sealed record CardPaymentSupervisorDetails(
    Guid AttemptGuid,
    CardProcessorKind Processor,
    string SessionId,
    Guid? OperationGuid,
    LocalCardPaymentAttemptStatus Status,
    DateTimeOffset UpdatedAt);

public sealed record CardPaymentSupervisorResolution(
    Guid AttemptGuid,
    CardProcessorKind Processor,
    CardPaymentSupervisorDecision Decision,
    string Reason,
    string OperatorCashierId,
    string? OperatorUserGuid = null,
    string? OperatorName = null,
    string? Evidence = null,
    string? PaymentReference = null);

public sealed record CardPaymentSupervisorResolutionResult(
    bool Succeeded,
    string Message,
    CardPaymentRecoveryResult? RecoveryResult = null,
    bool LockRetained = false,
    bool ResolutionPersisted = false,
    bool ResolutionApplied = false);

internal static class CardRefundSupervisorResolutionCodes
{
    public const string ConfirmedRefunded = "SUPERVISOR_CONFIRMED_REFUNDED";
    public const string ConfirmedNotRefunded = "SUPERVISOR_CONFIRMED_NOT_REFUNDED";
    public const string ContinueWaiting = "SUPERVISOR_CONTINUE_WAITING";
}

internal static class CardRefundSupervisorResolutionRules
{
    public static bool TryNormalize(
        CardRefundSupervisorResolution resolution,
        out CardRefundSupervisorResolution normalized,
        out string error)
    {
        var reason = Normalize(resolution.Reason);
        var evidence = Normalize(resolution.Evidence);
        var refundReference = Normalize(resolution.RefundReference);
        normalized = resolution with
        {
            Reason = reason ?? string.Empty,
            Evidence = evidence,
            RefundReference = refundReference
        };

        if (reason?.Length > 500 || evidence?.Length > 1000 || refundReference?.Length > 200)
        {
            error = "The supervisor note, evidence, or refund reference is too long.";
            return false;
        }

        if (resolution.Decision == CardRefundSupervisorDecision.ConfirmRefunded &&
            reason is null &&
            refundReference is null)
        {
            error = "Enter the bank refund reference or a supervisor note before confirming the refund.";
            return false;
        }

        if (resolution.Decision == CardRefundSupervisorDecision.ConfirmNotRefunded &&
            evidence is null)
        {
            error = "Enter the bank evidence confirming that no refund was processed.";
            return false;
        }

        if (resolution.Decision == CardRefundSupervisorDecision.ContinueWaiting &&
            reason is null)
        {
            error = "Enter a supervisor note before keeping the refund locked.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class CardPaymentSupervisorResolutionRules
{
    public static bool TryNormalize(
        CardPaymentSupervisorResolution resolution,
        out CardPaymentSupervisorResolution normalized,
        out string error,
        Func<string, string, string>? localize = null)
    {
        var reason = Normalize(resolution.Reason);
        var evidence = Normalize(resolution.Evidence);
        var paymentReference = Normalize(resolution.PaymentReference);
        var operatorCashierId = Normalize(resolution.OperatorCashierId);
        normalized = resolution with
        {
            Reason = reason ?? string.Empty,
            Evidence = evidence,
            // ContinueWaiting 不是金融结论，不能把恢复中心的共享输入固化成付款证据。
            PaymentReference = resolution.Decision == CardPaymentSupervisorDecision.ContinueWaiting
                ? null
                : paymentReference,
            OperatorCashierId = operatorCashierId ?? string.Empty,
            OperatorUserGuid = Normalize(resolution.OperatorUserGuid),
            OperatorName = Normalize(resolution.OperatorName)
        };

        if (reason?.Length > 500 ||
            evidence?.Length > 1000 ||
            paymentReference?.Length > 200 ||
            operatorCashierId is null)
        {
            error = "The supervisor identity, note, evidence, or payment reference is invalid.";
            return false;
        }

        if (resolution.Decision == CardPaymentSupervisorDecision.ConfirmPaid &&
            evidence is null &&
            paymentReference is null)
        {
            error = "Enter the bank payment reference or evidence before confirming payment.";
            return false;
        }

        if (resolution.Decision == CardPaymentSupervisorDecision.ConfirmNotPaid &&
            evidence is null)
        {
            error = "Enter bank evidence confirming that no payment was processed.";
            return false;
        }

        if (resolution.Decision == CardPaymentSupervisorDecision.ConfirmNotPaid &&
            paymentReference is not null)
        {
            const string fallback = "Do not enter a payment reference when confirming that no payment was processed.";
            error = localize?.Invoke("cardRecovery.linkly.notPaidPaymentReferenceNotAllowed", fallback) ?? fallback;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record CardPaymentRecoveryResult(
    CardPaymentRecoveryOutcome Outcome,
    string Message,
    LocalOrder? Order = null,
    decimal TenderedAmount = 0m,
    decimal ChangeAmount = 0m,
    PosSessionState? UpdatedSession = null,
    CardPaymentRecoveryDialogDetails? DialogDetails = null,
    CardPaymentRecoveryBankReceipt? BankReceipt = null,
    IReadOnlyList<PaymentTender>? RestoredTenders = null,
    CardRefundRecoveryDetails? RefundDetails = null,
    bool HasPostCommitWarning = false,
    CardPaymentSupervisorDetails? PaymentSupervisorDetails = null)
{
    public bool RequiresAlternativeRefundMethod { get; init; }

    // 需要 UI 原子投影后继续交接的草稿携带此键；attempt 会保持 FinalizePending，
    // 直到订单/草稿 handoff 完成后再由 CAS 终态化并释放精确 owner。
    internal CardRecoveryAttemptKey? DraftHandoffKey { get; init; }

    public static CardPaymentRecoveryResult None { get; } = new(CardPaymentRecoveryOutcome.None, string.Empty);
}

public sealed record CardPaymentRecoveryBankReceipt(
    string Environment,
    string SessionId,
    string ReceiptText,
    LinklyBankReceiptKind Kind,
    string? ResponseCode,
    string? ResponseText);

public sealed record CardPaymentRecoveryDialogDetails(
    string? SessionId,
    string? TxnRef,
    string? ResponseCode,
    string? ResponseText,
    decimal? Amount,
    DateTimeOffset Timestamp);

public interface ICardPaymentRecoveryService
{
    Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<CardPaymentRecoveryResult> RecoverActiveSessionAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<CardPaymentRecoveryResult> ManuallyClearActiveSessionAsync(
        string sessionId,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<CardRefundSupervisorResolutionResult> ResolveRefundAsync(
        CardRefundSupervisorResolution resolution,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CardRefundSupervisorResolutionResult(
            false,
            "Card refund supervisor resolution is unavailable."));

    Task<CardPaymentSupervisorResolutionResult> ResolvePaymentAsync(
        CardPaymentSupervisorResolution resolution,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CardPaymentSupervisorResolutionResult(
            false,
            "Card payment supervisor resolution is unavailable.",
            LockRetained: true));

    Task<IReadOnlyList<CardRecoveryQueueItem>> ListHistoryAsync(
        PosSessionState session, CancellationToken cancellationToken = default) =>
        ListOpenAsync(session, cancellationToken);

    Task<IReadOnlyList<CardRecoveryQueueItem>> ListOpenAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Card recovery queue is not wired for this service.");

    Task<CardPaymentRecoveryResult> RecoverAsync(
        CardRecoveryAttemptKey key,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Targeted card recovery is not wired for this service.");

    Task<CardRecoveryResolutionResult> ResolveAsync(
        CardRecoveryAttemptKey key,
        CardRecoverySupervisorDecision decision,
        string reason,
        string? evidence,
        string? reference,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Targeted card resolution is not wired for this service.");
}

public sealed class CardPaymentRecoveryService(
    ILocalCardPaymentAttemptRepository attemptRepository,
    ICardTerminalSettingsProvider settingsProvider,
    ILinklyBackendTerminalClient backendTerminalClient,
    CashCheckoutService checkout,
    ILocalOrderRepository orderRepository,
    ISyncQueueRepository syncQueueRepository,
    ILocalizationService? localization = null,
    ILinklyTerminalClient? linklyTerminalClient = null,
    FinancialSupervisorAuditReplayService? supervisorAuditReplay = null,
    ISharedHeldOrderRepository? sharedHeldOrderRepository = null,
    ILinklyCloudTerminalClient? cloudTerminalClient = null,
    ILinklyTerminalSelectionTransitionGate? linklyTerminalSelectionTransitionGate = null) : ICardPaymentRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISharedHeldOrderPaymentSourceResolver? _heldOrderPaymentSourceResolver =
        sharedHeldOrderRepository is null
            ? null
            : new SharedHeldOrderPaymentSourceResolver(
                sharedHeldOrderRepository,
                new SharedHeldOrderReverseMapper());

    private async Task<T> RunWithTerminalSelectionAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (linklyTerminalSelectionTransitionGate is null)
        {
            return await action();
        }

        // 仅包住会读取当前终端选择的请求；按 SessionId 的后续查询不依赖当前分配。
        await using var lease = await linklyTerminalSelectionTransitionGate.EnterFinancialOperationAsync(cancellationToken);
        return await action();
    }

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
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
        if (settings.Processor != CardProcessorKind.Linkly)
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

        if (mode != LinklyConnectionMode.CloudBackendAsync && mode != LinklyConnectionMode.LocalIp &&
            mode != LinklyConnectionMode.CloudDirectSync)
        {
            return CardPaymentRecoveryResult.None;
        }

        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetLatestOpenAttemptAsync(
            storeCode,
            deviceCode,
            // 中文注释：断电/退出恢复属于同一终端安全检查，不能被重启后的当前收银员阻断。
            cashierId: null,
            environment,
            cancellationToken),
            cancellationToken);
        LogRecoveryScan(settings, session, attempt);
        if (attempt is null)
        {
            LogRecoveryResult(settings, null, null, CardPaymentRecoveryOutcome.None, "no-open-attempt");
            return CardPaymentRecoveryResult.None;
        }

        return await RecoverSaleAttemptAsync(
            cart,
            session,
            settings,
            mode,
            attempt,
            cancellationToken);
    }

    private async Task<CardPaymentRecoveryResult> RecoverRefundAttemptAsync(
        PosCartService cart,
        PosSessionState session,
        CardTerminalSettings settings,
        LocalCardPaymentAttempt refundAttempt,
        CancellationToken cancellationToken)
    {
        if (IsFinalFailureAwaitingAcknowledgement(refundAttempt))
        {
            return await RetryFinalFailureAcknowledgementAsync(settings, refundAttempt, cancellationToken);
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

        // 已持久化的主管确认是人工核对结论，旧引用门禁只能阻止自动查询或重试，不能覆盖该结论。
        var refundMode = ResolveAttemptConnectionMode(
            refundAttempt,
            CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode));
        if (refundMode == LinklyConnectionMode.LocalIp &&
            !LinklyLocalTxnRef.TryNormalizeHistoricalReference(refundAttempt.TxnRef, out _))
        {
            LogRecoveryResult(
                settings,
                refundAttempt,
                null,
                CardPaymentRecoveryOutcome.Unknown,
                "refund-invalid-historical-txn-ref");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                LegacyLinklyTxnRefMessage(),
                DialogDetails: BuildDialogDetails(refundAttempt),
                RefundDetails: BuildRefundDetails(refundAttempt, CardProcessorKind.Linkly));
        }

        if (refundAttempt.Status == LocalCardPaymentAttemptStatus.Pending &&
            string.Equals(
                refundAttempt.ResponseCode,
                CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
                StringComparison.Ordinal))
        {
            return await RestoreSupervisorApprovedRetryAsync(cart, refundAttempt, cancellationToken);
        }

        // 未经主管核对的退款不能自动重发；启动恢复只维持锁并呈现三态结案入口。
        if (refundAttempt.Status == LocalCardPaymentAttemptStatus.Pending)
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
                        T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile the terminal and the original sale."),
                        DialogDetails: BuildDialogDetails(refundAttempt),
                        RefundDetails: BuildRefundDetails(refundAttempt, CardProcessorKind.Linkly));
            }

            refundAttempt = refundAttempt with
            {
                Status = LocalCardPaymentAttemptStatus.Recovering,
                UpdatedAt = recoveringAt
            };
        }

        ConsoleLog.Write(
            "CardRecovery",
            $"open refund requires reconciliation attemptGuid={refundAttempt.AttemptGuid} txnRef={LogValue(refundAttempt.TxnRef)} amount={refundAttempt.Amount:0.00}");
        LogRecoveryResult(settings, refundAttempt, null, CardPaymentRecoveryOutcome.Unknown, "refund-requires-reconciliation");
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile the terminal and the original sale."),
            DialogDetails: BuildDialogDetails(refundAttempt),
            RefundDetails: BuildRefundDetails(refundAttempt, CardProcessorKind.Linkly));
    }

    private async Task<CardPaymentRecoveryResult> RecoverSaleAttemptAsync(
        PosCartService cart,
        PosSessionState session,
        CardTerminalSettings settings,
        LinklyConnectionMode mode,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (IsFinalFailureAwaitingAcknowledgement(attempt))
        {
            return await RetryFinalFailureAcknowledgementAsync(settings, attempt, cancellationToken);
        }

        if (IsHistoricalSupervisorNotPaidAwaitingAcknowledgement(attempt))
        {
            return await ReplayHistoricalSupervisorNotPaidAcknowledgementAsync(
                settings,
                ResolveAttemptConnectionMode(attempt, mode),
                attempt,
                cancellationToken);
        }

        if (IsSupervisorResolvedPayment(attempt))
        {
            return await FinalizeSupervisorPaymentAsync(
                cart,
                session,
                settings,
                attempt,
                cancellationToken);
        }

        if (string.Equals(
                attempt.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            var pendingDraft = TryDeserializeDraft(attempt);
            if (pendingDraft is null ||
                !Enum.TryParse<LocalCardPaymentAttemptStatus>(
                    attempt.RecoveryTargetStatus,
                    ignoreCase: false,
                    out var pendingTarget))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                    DialogDetails: BuildDialogDetails(attempt),
                    PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
            }

            if (pendingTarget is
                LocalCardPaymentAttemptStatus.Declined or
                LocalCardPaymentAttemptStatus.TimedOut or
                LocalCardPaymentAttemptStatus.Cancelled or
                LocalCardPaymentAttemptStatus.Failed)
            {
                var pendingMode = ResolveAttemptConnectionMode(attempt, mode);
                Func<Task<bool>>? acknowledge = pendingMode == LinklyConnectionMode.CloudBackendAsync &&
                    !string.IsNullOrWhiteSpace(attempt.SessionId)
                    ? () => CompleteSupervisorAcknowledgeAsync(
                        settings,
                        attempt,
                        pendingMode,
                        CancellationToken.None)
                    : null;
                return await FinalizeDeclinedOrFailedAsync(
                    cart,
                    attempt,
                    pendingDraft,
                    pendingTarget,
                    attempt.ResponseCode,
                    attempt.ResponseText,
                    attempt.PaymentReference,
                    BuildDialogDetails(attempt),
                    string.IsNullOrWhiteSpace(attempt.ResponseText)
                        ? pendingTarget.ToString()
                        : attempt.ResponseText,
                    acknowledge,
                    cancellationToken);
            }

            if (pendingTarget is LocalCardPaymentAttemptStatus.OrderCompleted or LocalCardPaymentAttemptStatus.Approved &&
                attempt.Status == LocalCardPaymentAttemptStatus.Approved)
            {
                LocalOrder? existingOrder;
                try
                {
                    existingOrder = await RunLocalStoreAsync(
                        () => orderRepository.GetOrderAsync(pendingDraft.OrderGuid, CancellationToken.None),
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    TryWriteRecoveryLog(
                        $"recover finalize-pending existing-order query failed attemptGuid={attempt.AttemptGuid} orderGuid={pendingDraft.OrderGuid} error={ex.GetType().Name}",
                        ex,
                        attempt.AttemptGuid,
                        error: false);
                    return new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely rebuild the order. Ask a supervisor to confirm the payment before continuing."),
                        DialogDetails: BuildDialogDetails(attempt),
                        PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
                }

                if (existingOrder is not null)
                {
                    if (!HasExactAttemptTender(existingOrder, attempt.AttemptGuid))
                    {
                        return new CardPaymentRecoveryResult(
                            CardPaymentRecoveryOutcome.Unknown,
                            T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but the saved order does not contain matching payment evidence. Ask a supervisor to reconcile it before continuing."),
                            DialogDetails: BuildDialogDetails(attempt),
                            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
                    }

                    return await CompleteFinalizePendingExistingOrderAsync(
                        attempt,
                        pendingTarget,
                        existingOrder,
                        cancellationToken);
                }

                var pendingMode = ResolveAttemptConnectionMode(attempt, mode);
                if (pendingMode is LinklyConnectionMode.LocalIp or LinklyConnectionMode.CloudDirectSync)
                {
                    var authorization = new PaymentAuthorizationResult(
                        true,
                        Reference: attempt.PaymentReference ?? attempt.TxnRef ?? attempt.AttemptGuid.ToString("N"),
                        Message: attempt.ResponseText,
                        AuthorizedAmount: Math.Abs(pendingDraft.CardAmount),
                        Processor: CardProcessorKind.Linkly.ToString(),
                        Environment: attempt.Environment,
                        ConnectionMode: attempt.ConnectionMode,
                        TxnType: attempt.TxnType,
                        SessionId: attempt.SessionId,
                        TxnRef: attempt.TxnRef,
                        ResponseCode: attempt.ResponseCode,
                        ResponseText: attempt.ResponseText);
                    return await CompleteApprovedLocalAttemptAsync(
                        cart,
                        session,
                        attempt,
                        pendingDraft,
                        authorization,
                        cancellationToken);
                }

                return await CompleteApprovedAttemptAsync(
                    cart,
                    session,
                    settings,
                    attempt,
                    pendingDraft,
                    BuildSupervisorApprovedStatus(attempt),
                    cancellationToken);
            }

            // 已持久化的批准/订单目标由对应批准恢复路径续跑，禁止重新进入远端查询覆盖证据。
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely rebuild the order. Ask a supervisor to confirm the payment before continuing."),
                DialogDetails: BuildDialogDetails(attempt),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        var attemptMode = ResolveAttemptConnectionMode(attempt, mode);
        // 中文注释：直连没有后端 acknowledge 协议；已完成订单不能因保留 session 再次查询或开账。
        if (attemptMode == LinklyConnectionMode.CloudDirectSync &&
            attempt.Status == LocalCardPaymentAttemptStatus.OrderCompleted)
        {
            return CardPaymentRecoveryResult.None;
        }

        if (attempt.Status == LocalCardPaymentAttemptStatus.Approved)
        {
            // Approved 是已持久化的金融事实；直接续跑订单/草稿恢复，禁止迟到的远端失败结果覆盖它。
            var approvedDraft = TryDeserializeDraft(attempt);
            if (approvedDraft is null)
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely rebuild the order. Ask a supervisor to confirm the payment before continuing."),
                    DialogDetails: BuildDialogDetails(attempt),
                    PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
            }

            if (attemptMode is LinklyConnectionMode.LocalIp or LinklyConnectionMode.CloudDirectSync)
            {
                var authorization = new PaymentAuthorizationResult(
                    true,
                    Reference: attempt.PaymentReference ?? attempt.TxnRef ?? attempt.AttemptGuid.ToString("N"),
                    Message: attempt.ResponseText,
                    AuthorizedAmount: Math.Abs(approvedDraft.CardAmount),
                    Processor: CardProcessorKind.Linkly.ToString(),
                    Environment: attempt.Environment,
                    ConnectionMode: attempt.ConnectionMode,
                    TxnType: attempt.TxnType,
                    SessionId: attempt.SessionId,
                    TxnRef: attempt.TxnRef,
                    ResponseCode: attempt.ResponseCode,
                    ResponseText: attempt.ResponseText);
                return await CompleteApprovedLocalAttemptAsync(
                    cart,
                    session,
                    attempt,
                    approvedDraft,
                    authorization,
                    cancellationToken);
            }

            if (attemptMode == LinklyConnectionMode.CloudBackendAsync)
            {
                return await CompleteApprovedAttemptAsync(
                    cart,
                    session,
                    settings,
                    attempt,
                    approvedDraft,
                    BuildSupervisorApprovedStatus(attempt),
                    cancellationToken);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely rebuild the order. Ask a supervisor to confirm the payment before continuing."),
                DialogDetails: BuildDialogDetails(attempt),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (attempt.Status == LocalCardPaymentAttemptStatus.RequiresReview)
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover requires review attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} txnRef={LogValue(attempt.TxnRef)} amount={attempt.Amount:0.00}");
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Unknown, "requires-review");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.requiresReview", "The previous card amount does not match the order amount. Ask a supervisor to confirm the Linkly backend status before handling."),
                DialogDetails: BuildDialogDetails(attempt),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (attempt.Status == LocalCardPaymentAttemptStatus.OrderCompleted &&
            attempt.AcknowledgedAt is null &&
            !string.IsNullOrWhiteSpace(attempt.SessionId) &&
            attemptMode == LinklyConnectionMode.CloudBackendAsync)
        {
            await RetryCompletedAttemptAcknowledgeAsync(settings, attempt, cancellationToken);
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.OrderCompleted, "order-completed-ack-retry");
            return CardPaymentRecoveryResult.None;
        }

        if (attemptMode is LinklyConnectionMode.LocalIp or LinklyConnectionMode.CloudDirectSync)
        {
            return await RecoverClientTransactionAsync(cart, session, settings, attempt, cancellationToken);
        }

        if (attemptMode != LinklyConnectionMode.CloudBackendAsync)
        {
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.None, "unsupported-attempt-connection-mode");
            return CardPaymentRecoveryResult.None;
        }

        // 中文注释：先核验终端最终结果；草稿语义无效时仍要先保全已确认的金融结果，
        // 但不得物化或发布活动购物车。
        var draft = TryDeserializeDraft(attempt);
        var checkingMessage = Format("cardRecovery.linkly.checking", "A previous card transaction for {0:C2} was in progress before the POS closed. Checking the card terminal status.", attempt.Amount);
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
            return winner is not null &&
                (winner.Status != attempt.Status || winner.UpdatedAt != attempt.UpdatedAt ||
                 !string.Equals(winner.RecoveryPhase, attempt.RecoveryPhase, StringComparison.Ordinal))
                ? await RecoverSaleAttemptAsync(cart, session, settings, mode, winner, CancellationToken.None)
                : new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                    DialogDetails: BuildDialogDetails(attempt),
                    PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        attempt = attempt with
        {
            Status = LocalCardPaymentAttemptStatus.Recovering,
            UpdatedAt = recoveringAt
        };
        LogRecoveryMarkedRecovering(settings, attempt);

        LinklyCloudBackendSessionResponse? status = null;
        var statusFromResumable = string.IsNullOrWhiteSpace(attempt.SessionId);
        try
        {
            status = !statusFromResumable
                ? await backendTerminalClient.GetSessionStatusAsync(settings, attempt.SessionId!, cancellationToken)
                : await RunWithTerminalSelectionAsync(
                    () => backendTerminalClient.GetResumableSessionAsync(settings, cancellationToken),
                    cancellationToken);

            // 鏈?SessionId 浣嗗悗绔?session 宸茶繃鏈?娓呯悊锛屽厹搴曞皾璇?Resumable
            if (!statusFromResumable && status is null)
            {
                ConsoleLog.Write(
                    "CardRecovery",
                    $"recover session-status-null retrying-resumable attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)}");
                status = await RunWithTerminalSelectionAsync(
                    () => backendTerminalClient.GetResumableSessionAsync(settings, cancellationToken),
                    cancellationToken);
            }

            if (status is not null)
            {
                attempt = await BindRecoveredSessionAsync(attempt, status, cancellationToken);
            }

            if (status is not null && !IsFinal(status))
            {
                ConsoleLog.Write(
                    "CardRecovery",
                    $"recover pending resume start attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status.SessionId)} txnRef={LogValue(status.TxnRef)} status={status.Status}");
                status = await backendTerminalClient.ResumeSessionUntilFinalAsync(settings, status, cancellationToken);
                attempt = await BindRecoveredSessionAsync(attempt, status, cancellationToken);
            }
        }
        // 未知结果异常自带 session/txn 明细，不能再被通用失败文案覆盖。
        catch (LinklyBackendResultUnknownException ex)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover result-unknown attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status?.SessionId ?? attempt.SessionId)} txnRef={LogValue(status?.TxnRef ?? attempt.TxnRef)} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, "result-unknown", ex.GetType().Name);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ex.Message,
                DialogDetails: BuildDialogDetails(attempt, status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }
        // 本地停止等待后仍要保留未知结果语义，提醒人工确认 Linkly 后端状态。
        catch (LinklyBackendLocalCancelException ex)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover local-cancel-result-unknown attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status?.SessionId ?? attempt.SessionId)} txnRef={LogValue(status?.TxnRef ?? attempt.TxnRef)} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, "local-cancel-result-unknown", ex.GetType().Name);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.localCancelUnknown", "Stopped waiting for the previous card result locally, so the final Linkly backend result is still unknown. Ask a supervisor to confirm Linkly before continuing."),
                DialogDetails: BuildDialogDetails(attempt, status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover status failed attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, "status-query-failed", ex.GetType().Name);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                DialogDetails: BuildDialogDetails(attempt, status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (status is null)
        {
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Checking, "status-null");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Checking,
                checkingMessage,
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (!IsFinal(status))
        {
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Checking, "remote-status-not-final");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Checking,
                checkingMessage,
                DialogDetails: BuildDialogDetails(attempt, status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (!StatusMatchesAttempt(attempt, status, statusFromResumable, out var mismatchReason))
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover status mismatch attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} statusSessionId={LogValue(status.SessionId)} txnRef={LogValue(attempt.TxnRef)} statusTxnRef={LogValue(status.TxnRef)} reason={mismatchReason}");
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, mismatchReason);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                DialogDetails: BuildDialogDetails(attempt, status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (IsApproved(status))
        {
            // 与实时收款共用同一套证据核验，服务端没给卡交易明细时也不能跳过：那正是证据没核验上的情形
            // （例如终端批准的金额与请求不一致），跳过就等于拿草稿金额补造终端批准金额并自动落单。
            // 旧服务端不返回明细但会带原始 transaction 通知，ReadTransactionResult 会从通知里读出金额，兼容路径不受影响。
            var transactionResult = LinklyBackendTerminalClient.ReadTransactionResult(
                status,
                Math.Abs(attempt.Amount),
                attempt.TxnRef ?? string.Empty);
            var isAttemptAmountVerified = LinklyBackendTerminalClient.IsTransactionResultVerified(
                status,
                transactionResult,
                Math.Abs(attempt.Amount));
            var isDraftAmountVerified = draft is null || LinklyBackendTerminalClient.IsTransactionResultVerified(
                status,
                transactionResult,
                Math.Abs(draft.CardAmount));
            if (!transactionResult.Succeeded || !isAttemptAmountVerified || !isDraftAmountVerified)
            {
                // 先核验支付证据，即使草稿缺失也不能持久化未经验证的批准状态、保存订单或确认 session。
                LogRecoveryResult(
                    settings,
                    attempt,
                    status,
                    CardPaymentRecoveryOutcome.Unknown,
                    status.CardTransaction is null
                        ? "approved-transaction-evidence-missing"
                        : "approved-transaction-evidence-mismatch");
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                    DialogDetails: BuildDialogDetails(attempt, status),
                    PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
            }

            // 统一使用解析后的响应码、文案与终端引用。
            status = status with
            {
                TxnRef = NormalizeOptional(transactionResult.TxnRef) ?? status.TxnRef,
                ResponseCode = NormalizeOptional(transactionResult.ResponseCode) ?? status.ResponseCode,
                ResponseText = NormalizeOptional(transactionResult.ResponseText) ?? status.ResponseText
            };

            if (draft is null)
            {
                return await PersistInvalidRecoveredDraftAsync(
                    attempt,
                    LocalCardPaymentAttemptStatus.OrderCompleted,
                    status.ResponseCode,
                    status.ResponseText,
                    BuildPaymentReference(attempt, status),
                    BuildDialogDetails(attempt, status),
                    cancellationToken);
            }

            var result = await CompleteApprovedAttemptAsync(cart, session, settings, attempt, draft, status, cancellationToken);
            var reason = result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted
                ? "approved-order-completed"
                : result.Outcome == CardPaymentRecoveryOutcome.DraftRestored
                    ? "approved-tender-restored"
                    : "approved-requires-review";
            LogRecoveryResult(settings, attempt, status, result.Outcome, reason);
            return result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted
                ? result with { Message = T("cardRecovery.linkly.approved", "The previous card payment was successful. The order has been recovered and saved automatically.") }
                : result with { PaymentSupervisorDetails = BuildPaymentSupervisorDetails(attempt) };
        }

        if (!cart.IsEmpty)
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover deferred current-cart-not-empty attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} statusSessionId={LogValue(status.SessionId)} outcome={status.Status}");
            LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, "current-cart-not-empty");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.currentCartNotEmpty", "The previous card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order."),
                DialogDetails: BuildDialogDetails(attempt, status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (IsDeclinedOrFailed(status))
        {
            var reason = string.IsNullOrWhiteSpace(status.ResponseText) ? status.Status : status.ResponseText;
            if (draft is null)
            {
                return await PersistInvalidRecoveredDraftAsync(
                    attempt,
                    MapFailureStatus(status),
                    status.ResponseCode,
                    status.ResponseText,
                    attempt.PaymentReference,
                    BuildDialogDetails(attempt, status),
                    cancellationToken);
            }

            var result = await FinalizeDeclinedOrFailedAsync(
                cart,
                attempt,
                draft,
                MapFailureStatus(status),
                status.ResponseCode,
                status.ResponseText,
                attempt.PaymentReference,
                BuildDialogDetails(attempt, status),
                reason,
                () => CompleteSupervisorAcknowledgeAsync(
                    settings,
                    attempt,
                    LinklyConnectionMode.CloudBackendAsync,
                    cancellationToken),
                cancellationToken);
            LogRecoveryResult(settings, attempt, status, result.Outcome, "declined-or-failed");
            return result;
        }

        LogRecoveryResult(settings, attempt, status, CardPaymentRecoveryOutcome.Unknown, "unhandled-final-status");
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
            DialogDetails: BuildDialogDetails(attempt, status),
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
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
            .Select(attempt => new CardRecoveryQueueItem(
                CardProcessorKind.Linkly,
                attempt.AttemptGuid,
                attempt.OperationKind,
                attempt.Amount,
                attempt.StoreCode,
                attempt.DeviceCode,
                attempt.CashierId,
                attempt.Environment,
                string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal)
                    ? CardRecoveryPhases.FinalizePending
                    : attempt.Status.ToString(),
                attempt.CreatedAt,
                attempt.UpdatedAt,
                attempt.OrderDraftJson,
                attempt.SessionId,
                attempt.TxnRef,
                null,
                attempt.ResponseCode,
                attempt.ResponseText,
                attempt.PaymentReference,
                null,
                attempt.OperationGuid))
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
        return attempts
            .Select(attempt => new CardRecoveryQueueItem(
                CardProcessorKind.Linkly,
                attempt.AttemptGuid,
                attempt.OperationKind,
                attempt.Amount,
                attempt.StoreCode,
                attempt.DeviceCode,
                attempt.CashierId,
                attempt.Environment,
                string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal)
                    ? CardRecoveryPhases.FinalizePending
                    : attempt.Status.ToString(),
                attempt.CreatedAt,
                attempt.UpdatedAt,
                attempt.OrderDraftJson,
                attempt.SessionId,
                attempt.TxnRef,
                null,
                attempt.ResponseCode,
                attempt.ResponseText,
                attempt.PaymentReference,
                null,
                attempt.OperationGuid))
            .ToArray();
    }

    public async Task<CardPaymentRecoveryResult> RecoverAttemptAsync(
        Guid attemptGuid,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attemptGuid, cancellationToken),
            cancellationToken);
        if (attempt is null ||
            !string.Equals(attempt.Processor, CardProcessorKind.Linkly.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return CardPaymentRecoveryResult.None;
        }

        if (IsHistoricalSupervisorNotPaidAwaitingAcknowledgement(attempt))
        {
            return await ReplayHistoricalSupervisorNotPaidAcknowledgementAsync(
                settings,
                mode,
                attempt,
                cancellationToken);
        }

        // 已落库失败的收尾只重试 ack，必须在终态门禁和任何重新查询/草稿恢复之前处理。
        if (IsFinalFailureAwaitingAcknowledgement(attempt))
        {
            return await RetryFinalFailureAcknowledgementAsync(settings, attempt, cancellationToken);
        }

        // 定点恢复必须先在 MarkRecovering 前拒绝真正终态，避免重复进入恢复并覆盖已落库结果。
        if (IsTerminalRecoveryStatus(attempt))
        {
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.None, "terminal-attempt");
            return CardPaymentRecoveryResult.None;
        }

        if (string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase))
        {
            return await RecoverRefundAttemptAsync(cart, session, settings, attempt, cancellationToken);
        }

        if (string.Equals(attempt.OperationKind, "ActiveSession", StringComparison.OrdinalIgnoreCase))
        {
            // 定点恢复：必须使用选中 attempt 的 SessionId，绝不能回退到 latest-only active session。
            return await RecoverActiveSessionAttemptAsync(cart, session, settings, attempt, cancellationToken);
        }

        LogRecoveryScan(settings, session, attempt);
        return await RecoverSaleAttemptAsync(cart, session, settings, mode, attempt, cancellationToken);
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
            !string.Equals(attempt.Processor, CardProcessorKind.Linkly.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new CardRecoveryResolutionResult(
                false,
                "The unresolved attempt no longer matches this terminal and cannot be changed.");
        }

        if (string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase))
        {
            var refundResult = await ResolveRefundAsync(
                new CardRefundSupervisorResolution(
                    attempt.AttemptGuid,
                    CardProcessorKind.Linkly,
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

        var authorizer = OperationAuthorizationScope.CurrentAuthorizingSession ?? session.CashierSession;
        var paymentResult = await ResolvePaymentAsync(
            new CardPaymentSupervisorResolution(
                attempt.AttemptGuid,
                CardProcessorKind.Linkly,
                MapPaymentDecision(decision),
                reason,
                authorizer?.CashierId ?? session.CashierId,
                authorizer?.UserGuid ?? session.CashierSession?.UserGuid,
                authorizer?.CashierName ?? session.CashierName,
                evidence,
                reference),
            cart,
            session,
            cancellationToken);
        return new CardRecoveryResolutionResult(
            paymentResult.Succeeded,
            paymentResult.Message,
            paymentResult.RecoveryResult,
            LockRetained: paymentResult.LockRetained,
            ResolutionPersisted: paymentResult.ResolutionPersisted,
            ResolutionApplied: paymentResult.ResolutionApplied);
    }

    private async Task<CardPaymentRecoveryResult> RecoverActiveSessionAttemptAsync(
        PosCartService cart,
        PosSessionState session,
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (IsSupervisorResolvedActiveSession(attempt))
        {
            return await FinalizeSupervisorPaymentAsync(cart, session, settings, attempt, cancellationToken);
        }

        var sessionId = NormalizeOptional(attempt.SessionId);
        if (sessionId is null)
        {
            return BuildUnresolvedActiveSessionResult(
                attempt,
                T("cardRecovery.linkly.activeSessionUnknown", "The previous Linkly session cannot be confirmed. Ask a supervisor to check Linkly before charging again."));
        }

        LinklyCloudBackendSessionResponse? status = null;
        try
        {
            // 定点恢复：按选中 attempt 的 SessionId 查询，绝不无条件采用后端最新 resumable session。
            status = await backendTerminalClient.GetSessionStatusAsync(settings, sessionId, cancellationToken);
            if (status is null)
            {
                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("cardRecovery.linkly.activeSessionUnknown", "The previous Linkly session cannot be confirmed. Ask a supervisor to check Linkly before charging again."));
            }

            attempt = await BindRecoveredSessionAsync(attempt, status, cancellationToken);
            if (!IsFinal(status))
            {
                status = await backendTerminalClient.ResumeSessionUntilFinalAsync(settings, status, cancellationToken);
                attempt = await BindRecoveredSessionAsync(attempt, status, cancellationToken);
            }

            if (string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
                status.TransactionSuccess is null)
            {
                status = await backendTerminalClient.GetSessionStatusAsync(settings, status.SessionId, cancellationToken);
            }
        }
        catch (LinklyBackendResultUnknownException ex)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover targeted active-session result-unknown attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status?.SessionId ?? attempt.SessionId)} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ex.Message,
                DialogDetails: BuildDialogDetails(status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }
        catch (LinklyBackendLocalCancelException ex)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover targeted active-session local-cancel-result-unknown attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status?.SessionId ?? attempt.SessionId)} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionLocalCancelUnknown", "Stopped waiting for the previous Linkly session locally, so the final result is still unknown. Ask a supervisor to confirm Linkly before charging again."),
                DialogDetails: BuildDialogDetails(status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover targeted active-session failed attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status?.SessionId ?? attempt.SessionId)} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionUnknown", "The previous Linkly session cannot be confirmed. Ask a supervisor to check Linkly before charging again."),
                DialogDetails: BuildDialogDetails(status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (!IsFinal(status))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Checking,
                T("cardRecovery.linkly.activeSessionStillPending", "The previous Linkly session is still pending. Try recovery again or ask a supervisor to check Linkly."),
                DialogDetails: BuildDialogDetails(status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (IsApproved(status))
        {
            if (!await TrySaveActiveSessionOutcomeAsync(
                    attempt,
                    LocalCardPaymentAttemptStatus.Approved,
                    status,
                    cancellationToken))
            {
                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("payment.card.resultUnknown", "The card result is unknown. Do not collect payment again until recovery is completed."));
            }

            if (!await TryAcknowledgeActiveSessionAsync(settings, status, attempt, cancellationToken))
            {
                return ActiveSessionAcknowledgeFailed(status, attempt);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionApproved,
                T("cardRecovery.linkly.activeSessionApprovedCleared", "The previous Linkly transaction was successful and has been cleared. Continue the current order."),
                DialogDetails: BuildDialogDetails(status),
                BankReceipt: BuildActiveSessionBankReceipt(status, LinklyBankReceiptKind.RecoveredApproved));
        }

        if (IsDeclinedOrFailed(status))
        {
            if (!await TrySaveActiveSessionOutcomeAsync(
                    attempt,
                    MapFailureStatus(status),
                    status,
                    cancellationToken))
            {
                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("payment.card.resultUnknown", "The card result is unknown. Do not collect payment again until recovery is completed."));
            }

            if (!await TryAcknowledgeActiveSessionAsync(settings, status, attempt, cancellationToken))
            {
                return ActiveSessionAcknowledgeFailed(status, attempt);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionNotPaid,
                T("cardRecovery.linkly.activeSessionNotPaidCleared", "The previous Linkly transaction was not paid successfully and has been cleared. Continue the current order and retry payment if needed."),
                DialogDetails: BuildDialogDetails(status),
                BankReceipt: BuildActiveSessionBankReceipt(status, LinklyBankReceiptKind.RecoveredFailed));
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.linkly.activeSessionUnknown", "The previous Linkly session cannot be confirmed. Ask a supervisor to check Linkly before charging again."),
            DialogDetails: BuildDialogDetails(status),
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
    }

    private static CardRefundSupervisorDecision MapRefundDecision(CardRecoverySupervisorDecision decision) => decision switch
    {
        CardRecoverySupervisorDecision.ConfirmProcessed => CardRefundSupervisorDecision.ConfirmRefunded,
        CardRecoverySupervisorDecision.ConfirmNotProcessed => CardRefundSupervisorDecision.ConfirmNotRefunded,
        _ => CardRefundSupervisorDecision.ContinueWaiting
    };

    private static CardPaymentSupervisorDecision MapPaymentDecision(CardRecoverySupervisorDecision decision) => decision switch
    {
        CardRecoverySupervisorDecision.ConfirmProcessed => CardPaymentSupervisorDecision.ConfirmPaid,
        CardRecoverySupervisorDecision.ConfirmNotProcessed => CardPaymentSupervisorDecision.ConfirmNotPaid,
        _ => CardPaymentSupervisorDecision.ContinueWaiting
    };

    private async Task<CardPaymentRecoveryResult> RecoverClientTransactionAsync(
        PosCartService cart,
        PosSessionState currentSession,
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (!LinklyLocalTxnRef.TryNormalizeHistoricalReference(attempt.TxnRef, out var txnRef))
        {
            LogRecoveryResult(
                settings,
                attempt,
                null,
                CardPaymentRecoveryOutcome.Unknown,
                "local-invalid-historical-txn-ref");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                LegacyLinklyTxnRefMessage(),
                DialogDetails: BuildDialogDetails(attempt),
                PaymentSupervisorDetails: BuildLegacyPaymentSupervisorDetails(attempt));
        }

        var directCloud = ResolveAttemptConnectionMode(attempt, settings.LinklyConnectionMode) == LinklyConnectionMode.CloudDirectSync;
        if (directCloud
            ? cloudTerminalClient is null || string.IsNullOrWhiteSpace(attempt.SessionId)
            : linklyTerminalClient is null)
        {
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Unknown, "local-client-unavailable");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                DialogDetails: BuildDialogDetails(attempt),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        // 中文注释：语义无效草稿不能阻止本地终端核验；批准结果仍先落为待最终化，
        // 随后以 Unknown 返回，避免活动购物车被触碰。
        var draft = TryDeserializeDraft(attempt);
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
            return winner is not null &&
                (winner.Status != attempt.Status || winner.UpdatedAt != attempt.UpdatedAt ||
                 !string.Equals(winner.RecoveryPhase, attempt.RecoveryPhase, StringComparison.Ordinal))
                ? await RecoverSaleAttemptAsync(
                    cart,
                    currentSession,
                    settings,
                    directCloud ? LinklyConnectionMode.CloudDirectSync : LinklyConnectionMode.LocalIp,
                    winner,
                    CancellationToken.None)
                : new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                    DialogDetails: BuildDialogDetails(attempt),
                    PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        attempt = attempt with
        {
            Status = LocalCardPaymentAttemptStatus.Recovering,
            UpdatedAt = recoveringAt
        };
        LogRecoveryMarkedRecovering(settings, attempt);

        PaymentAuthorizationResult authorization;
        try
        {
            // 中文注释：按交易冻结的模式只查询原交易；直连使用持久化的 session，禁止重发扣款。
            authorization = await RunWithTerminalSelectionAsync(
                () => directCloud
                    ? cloudTerminalClient!.RecoverTransactionAsync(
                        attempt.Amount, draft?.Session ?? currentSession,
                        settings with { LinklyConnectionMode = LinklyConnectionMode.CloudDirectSync },
                        attempt.SessionId!, txnRef, cancellationToken)
                    : linklyTerminalClient!.RecoverLastTransactionAsync(
                        attempt.Amount, draft?.Session ?? currentSession, settings, txnRef, cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover local-ip get-last failed attemptGuid={attempt.AttemptGuid} txnRef={LogValue(txnRef)} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Unknown, "local-get-last-failed", ex.GetType().Name);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                DialogDetails: BuildDialogDetails(attempt),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (!authorization.ResultUnknown &&
            (authorization.Approved || HasLocalFinalResult(authorization)) &&
            !LocalAuthorizationMatchesAttempt(attempt, authorization))
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover local-ip identity mismatch attemptGuid={attempt.AttemptGuid} expectedTxnRef={LogValue(attempt.TxnRef)} actualTxnRef={LogValue(ResolveAuthorizationTxnRef(authorization))} expectedTxnType={LogValue(attempt.TxnType)} actualTxnType={LogValue(authorization.TxnType)} expectedAmount={attempt.Amount:0.00} actualAmount={authorization.AuthorizedAmount:0.00}");
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Unknown, "local-identity-mismatch");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                DialogDetails: BuildDialogDetails(attempt, authorization),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (authorization.Approved && !authorization.ResultUnknown)
        {
            if (draft is null)
            {
                var responseTransaction = authorization.CardTransactions?.FirstOrDefault();
                return await PersistInvalidRecoveredDraftAsync(
                    attempt,
                    LocalCardPaymentAttemptStatus.OrderCompleted,
                    responseTransaction?.ResponseCode ?? authorization.ResponseCode,
                    responseTransaction?.ResponseText ?? authorization.ResponseText,
                    BuildLocalPaymentReference(attempt, authorization),
                    BuildDialogDetails(attempt, authorization),
                    cancellationToken);
            }

            var result = await CompleteApprovedLocalAttemptAsync(cart, currentSession, attempt, draft, authorization, cancellationToken);
            var reason = result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted
                ? "local-approved-order-completed"
                : result.Outcome == CardPaymentRecoveryOutcome.DraftRestored
                    ? "local-approved-tender-restored"
                    : "local-approved-requires-review";
            LogRecoveryResult(settings, attempt, null, result.Outcome, reason);
            return result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted
                ? result with { Message = T("cardRecovery.linkly.approved", "The previous card payment was successful. The order has been recovered and saved automatically.") }
                : result with { PaymentSupervisorDetails = BuildPaymentSupervisorDetails(attempt) };
        }

        if (HasLocalFinalResult(authorization) && !cart.IsEmpty)
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"recover local-ip deferred current-cart-not-empty attemptGuid={attempt.AttemptGuid} txnRef={LogValue(txnRef)}");
            LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Unknown, "current-cart-not-empty");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.currentCartNotEmpty", "The previous card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order."),
                DialogDetails: BuildDialogDetails(attempt, authorization),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (HasLocalFinalResult(authorization))
        {
            var transaction = authorization.CardTransactions?.FirstOrDefault();
            var responseCode = transaction?.ResponseCode ?? authorization.ResponseCode;
            var responseText = transaction?.ResponseText ?? authorization.ResponseText ?? authorization.Message;
            var reason = string.IsNullOrWhiteSpace(responseText) ? T("cardRecovery.linkly.failedReasonFallback", "Not approved") : responseText;
            if (draft is null)
            {
                return await PersistInvalidRecoveredDraftAsync(
                    attempt,
                    MapLocalFailureStatus(authorization),
                    responseCode,
                    responseText,
                    authorization.Reference ?? attempt.PaymentReference,
                    BuildDialogDetails(attempt, authorization),
                    cancellationToken);
            }

            var result = await FinalizeDeclinedOrFailedAsync(
                cart,
                attempt,
                draft,
                MapLocalFailureStatus(authorization),
                responseCode,
                responseText,
                authorization.Reference ?? attempt.PaymentReference,
                BuildDialogDetails(attempt, authorization),
                reason,
                acknowledgeAsync: null,
                cancellationToken);
            LogRecoveryResult(settings, attempt, null, result.Outcome, "local-declined-or-failed");
            return result;
        }

        LogRecoveryResult(settings, attempt, null, CardPaymentRecoveryOutcome.Unknown, "local-result-unknown");
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
            DialogDetails: BuildDialogDetails(attempt, authorization),
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
    }

    public async Task<CardRefundSupervisorResolutionResult> ResolveRefundAsync(
        CardRefundSupervisorResolution resolution,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        if (resolution.Processor != CardProcessorKind.Linkly)
        {
            return new CardRefundSupervisorResolutionResult(false, "The selected refund does not belong to Linkly.");
        }

        if (!CardRefundSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out var validationError))
        {
            return new CardRefundSupervisorResolutionResult(false, validationError);
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(normalized.AttemptGuid, cancellationToken),
            cancellationToken);
        if (attempt is null ||
            !string.Equals(attempt.Processor, CardProcessorKind.Linkly.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new CardRefundSupervisorResolutionResult(
                false,
                "The unresolved Linkly refund no longer matches this terminal and cannot be changed.");
        }

        var attemptMode = ResolveAttemptConnectionMode(
            attempt,
            CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode));
        var retryTxnRef = normalized.Decision == CardRefundSupervisorDecision.ConfirmNotRefunded
            ? BuildSupervisorRetryTxnRef(
                attemptMode,
                attempt.TxnRef,
                TryDeserializeDraft(attempt)?.OriginalReference)
            : null;
        var resolvedAt = DateTimeOffset.UtcNow;
        var journal = BuildRefundSupervisorJournal(
            attempt,
            normalized,
            session,
            retryTxnRef,
            resolvedAt);
        var applied = await RunLocalStoreAsync(
            () => attemptRepository.ResolveRefundWithJournalAsync(
                new CardRefundAttemptResolution(
                    normalized.AttemptGuid,
                    normalized.Decision,
                    normalized.Reason,
                    normalized.Evidence,
                    normalized.RefundReference,
                    retryTxnRef,
                    resolvedAt),
                attempt.Status,
                attempt.UpdatedAt,
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
                    CardRefundSupervisorResolutionCodes.ContinueWaiting,
                    StringComparison.Ordinal))
            {
                return new CardRefundSupervisorResolutionResult(
                    true,
                    T("cardRecovery.refund.waitingSaved", "The refund remains locked. Run recovery again after the bank result is available."),
                    LockRetained: true,
                    ResolutionPersisted: true);
            }

            if (winner is not null &&
                string.Equals(
                    winner.ResponseCode,
                    CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
                    StringComparison.Ordinal))
            {
                // 中文注释：CAS 失败方只能观察赢家，不能代替赢家推进终端、购物车或订单副作用。
                return new CardRefundSupervisorResolutionResult(
                    false,
                    ResolutionPendingMessage(),
                    RetryAllowed: false,
                    LockRetained: true,
                    ResolutionPersisted: true);
            }

            if (winner is not null &&
                string.Equals(
                    winner.ResponseCode,
                    CardRefundSupervisorResolutionCodes.ConfirmedRefunded,
                    StringComparison.Ordinal))
            {
                // 中文注释：相反决议已赢得 CAS；失败方保留其 FinalizePending 状态，由正常恢复流程继续。
                return new CardRefundSupervisorResolutionResult(
                    false,
                    ResolutionPendingMessage(),
                    LockRetained: true,
                    ResolutionPersisted: true);
            }

            return new CardRefundSupervisorResolutionResult(
                false,
                "The refund state changed before the supervisor decision was saved. Run recovery again.");
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

        LocalCardPaymentAttempt? updatedAttempt;
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
            $"supervisor refund resolution saved attemptGuid={attempt.AttemptGuid} decision={normalized.Decision} retryTxnRef={LogValue(retryTxnRef)}");

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
                retryAllowed
                    ? T("cardRecovery.refund.retryAllowed", "The bank confirmed that no refund was processed. The original return is ready to retry with the same operation.")
                    : ResolutionPendingMessage(),
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
        var confirmedSucceeded = completed.Outcome is
            CardPaymentRecoveryOutcome.OrderCompleted or
            CardPaymentRecoveryOutcome.DraftRestored;
        var confirmedLockRetained = await IsResolutionLockRetainedAsync(
            updatedAttempt.AttemptGuid,
            confirmedSucceeded);
        return new CardRefundSupervisorResolutionResult(
            confirmedSucceeded,
            confirmedSucceeded
                ? T("cardRecovery.refund.confirmedSaved", "The confirmed refund was recorded and the original return was recovered.")
                : ResolutionPendingMessage(),
            completed,
            LockRetained: confirmedLockRetained,
            ResolutionPersisted: true,
            ResolutionApplied: true);
    }

    public async Task<CardPaymentSupervisorResolutionResult> ResolvePaymentAsync(
        CardPaymentSupervisorResolution resolution,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        if (resolution.Processor != CardProcessorKind.Linkly)
        {
            return new CardPaymentSupervisorResolutionResult(
                false,
                "The selected payment does not belong to Linkly.",
                LockRetained: true);
        }

        if (!CardPaymentSupervisorResolutionRules.TryNormalize(
                resolution,
                out var normalized,
                out var validationError,
                T))
        {
            return new CardPaymentSupervisorResolutionResult(
                false,
                validationError,
                LockRetained: true);
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(normalized.AttemptGuid, cancellationToken),
            cancellationToken);
        var sessionKey = NormalizeOptional(attempt?.SessionId) ?? NormalizeOptional(attempt?.TxnRef);
        if (attempt is null ||
            sessionKey is null ||
            !string.Equals(attempt.Processor, CardProcessorKind.Linkly.ToString(), StringComparison.OrdinalIgnoreCase) ||
            attempt.OperationKind is not ("Sale" or "ActiveSession") ||
            !string.Equals(attempt.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new CardPaymentSupervisorResolutionResult(
                false,
                "The unresolved Linkly payment no longer matches this terminal and cannot be changed.",
                LockRetained: true);
        }

        if (IsPersistedTerminalStatus(attempt.Status) &&
            !string.Equals(
                attempt.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            var acknowledgementPending =
                attempt.Status == LocalCardPaymentAttemptStatus.OrderCompleted &&
                attempt.AcknowledgedAt is null &&
                !string.IsNullOrWhiteSpace(attempt.SessionId);
            return new CardPaymentSupervisorResolutionResult(
                false,
                T(
                    "cardRecovery.linkly.paymentAlreadyFinalized",
                    "The selected payment has already been finalized and cannot be changed."),
                LockRetained: acknowledgementPending,
                ResolutionPersisted: IsSupervisorResolvedPayment(attempt));
        }

        if (HasPersistedLinklyPaymentEvidence(attempt))
        {
            return new CardPaymentSupervisorResolutionResult(
                false,
                T(
                    "cardRecovery.linkly.paymentEvidenceExists",
                    "Bank payment evidence already exists. Run recovery instead."),
                LockRetained: true);
        }

        var resolvedAt = DateTimeOffset.UtcNow;
        var repositoryResolution = new ActiveSessionResolution(
            attempt.AttemptGuid,
            sessionKey,
            normalized.Decision switch
            {
                CardPaymentSupervisorDecision.ConfirmPaid => ActiveSessionSupervisorDecision.ConfirmPaid,
                CardPaymentSupervisorDecision.ConfirmNotPaid => ActiveSessionSupervisorDecision.ConfirmNotPaid,
                _ => ActiveSessionSupervisorDecision.ContinueWaiting
            },
            attempt.Status,
            attempt.UpdatedAt,
            normalized.Reason,
            normalized.Evidence,
            normalized.PaymentReference,
            resolvedAt);
        var journal = BuildPaymentSupervisorJournal(
            attempt,
            sessionKey,
            normalized,
            resolvedAt);
        var applied = await RunLocalStoreAsync(
            () => attemptRepository.ResolvePaymentWithJournalAsync(
                repositoryResolution,
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
                    ActiveSessionSupervisorResolutionCodes.ContinueWaiting,
                    StringComparison.Ordinal))
            {
                return new CardPaymentSupervisorResolutionResult(
                    false,
                    T("cardRecovery.linkly.supervisorWaiting", "The payment remains locked. Run recovery again after the bank result is available."),
                    LockRetained: true,
                    ResolutionPersisted: true);
            }

            if (winner is not null && IsSupervisorResolvedPayment(winner))
            {
                // CAS 失败表示本次调用没有取得结案权；只识别 winner，正式恢复必须由后续恢复入口执行。
                return new CardPaymentSupervisorResolutionResult(
                    false,
                    ResolutionPendingMessage(),
                    LockRetained: true,
                    ResolutionPersisted: true,
                    ResolutionApplied: false);
            }

            return new CardPaymentSupervisorResolutionResult(
                false,
                "The payment state changed before the supervisor decision was saved. Run recovery again.",
                LockRetained: true);
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

        LocalCardPaymentAttempt? updatedAttempt;
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
            return new CardPaymentSupervisorResolutionResult(
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
            return new CardPaymentSupervisorResolutionResult(
                false,
                ResolutionPendingMessage(),
                LockRetained: true,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        if (normalized.Decision == CardPaymentSupervisorDecision.ContinueWaiting)
        {
            var waiting = new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.supervisorWaiting", "The payment remains locked. Run recovery again after the bank result is available."),
                DialogDetails: BuildDialogDetails(updatedAttempt),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(updatedAttempt));
            return new CardPaymentSupervisorResolutionResult(
                true,
                waiting.Message,
                waiting,
                LockRetained: true,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        var completed = await RunPersistedResolutionRecoveryAsync(
            updatedAttempt.AttemptGuid,
            () => FinalizeSupervisorPaymentAsync(
                cart,
                session,
                settings,
                updatedAttempt,
                CancellationToken.None));
        var succeeded = completed.Outcome is
            CardPaymentRecoveryOutcome.OrderCompleted or
            CardPaymentRecoveryOutcome.DraftRestored;
        var lockRetained = await IsResolutionLockRetainedAsync(updatedAttempt.AttemptGuid, succeeded);
        return new CardPaymentSupervisorResolutionResult(
            succeeded,
            succeeded || !lockRetained
                ? completed.Message
                : ResolutionPendingMessage(),
            completed,
            LockRetained: lockRetained,
            ResolutionPersisted: true,
            ResolutionApplied: true);
    }

    public async Task<CardPaymentRecoveryResult> RecoverActiveSessionAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Linkly ||
            !CanRecoverBackendActiveSession(settings))
        {
            return CardPaymentRecoveryResult.None;
        }

        var persistedAttempt = await RunLocalStoreAsync(
            () => attemptRepository.GetLatestOpenActiveSessionAsync(
                session.StoreCode,
                session.DeviceCode,
                settings.Environment.ToString(),
                cancellationToken),
            cancellationToken);
        if (persistedAttempt is not null &&
            IsHistoricalSupervisorNotPaidAwaitingAcknowledgement(persistedAttempt))
        {
            return await ReplayHistoricalSupervisorNotPaidAcknowledgementAsync(
                settings,
                LinklyConnectionMode.CloudBackendAsync,
                persistedAttempt!,
                cancellationToken);
        }

        if (IsSupervisorResolvedActiveSession(persistedAttempt))
        {
            return await FinalizeSupervisorPaymentAsync(
                cart,
                session,
                settings,
                persistedAttempt!,
                cancellationToken);
        }

        LinklyCloudBackendSessionResponse? status = null;
        try
        {
            status = await RunWithTerminalSelectionAsync(
                () => backendTerminalClient.GetResumableSessionAsync(settings, cancellationToken),
                cancellationToken);
            if (status is null)
            {
                if (persistedAttempt is not null)
                {
                    return BuildUnresolvedActiveSessionResult(
                        persistedAttempt,
                        T("cardRecovery.linkly.activeSessionUnknown", "The previous Linkly session cannot be confirmed. Ask a supervisor to check Linkly before charging again."));
                }

                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.None,
                    T("cardRecovery.linkly.noActiveSession", "No unfinished Linkly session was found. You can try the card payment again."));
            }

            persistedAttempt = await PersistActiveSessionAsync(
                settings,
                session,
                status,
                cancellationToken);
            if (IsSupervisorResolvedActiveSession(persistedAttempt))
            {
                return await FinalizeSupervisorPaymentAsync(
                    cart,
                    session,
                    settings,
                    persistedAttempt,
                    cancellationToken);
            }

            // 付款页按钮只处理后端 active/resumable session，不能把它和当前购物车自动合并。
            if (!IsFinal(status))
            {
                ConsoleLog.Write(
                    "CardRecovery",
                    $"recover active-session resume start sessionId={LogValue(status.SessionId)} txnRef={LogValue(status.TxnRef)} status={status.Status}");
                status = await backendTerminalClient.ResumeSessionUntilFinalAsync(settings, status, cancellationToken);
            }

            if (string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
                status.TransactionSuccess is null)
            {
                // 中文注释：resumable 可能只返回 active session 摘要；Completed 但没有成功/失败位时必须按 SessionId 再查一次权威状态。
                ConsoleLog.Write(
                    "CardRecovery",
                    $"recover active-session refresh final summary sessionId={LogValue(status.SessionId)} txnRef={LogValue(status.TxnRef)} status={status.Status}");
                status = await backendTerminalClient.GetSessionStatusAsync(settings, status.SessionId, cancellationToken);
            }
        }
        // 未知结果异常自带 session/txn 明细，不能再被付款页的兜底文案覆盖。
        catch (LinklyBackendResultUnknownException ex)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover active-session result-unknown sessionId={LogValue(status?.SessionId)} txnRef={LogValue(status?.TxnRef)} error={ex.GetType().Name}",
                RecoveryLogContext(status?.SessionId),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ex.Message,
                DialogDetails: BuildDialogDetails(status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(persistedAttempt));
        }
        // 本地停止等待后要明确告诉收银员结果未知，而不是落回通用 active-session 失败文案。
        catch (LinklyBackendLocalCancelException ex)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover active-session local-cancel-result-unknown sessionId={LogValue(status?.SessionId)} txnRef={LogValue(status?.TxnRef)} error={ex.GetType().Name}",
                RecoveryLogContext(status?.SessionId),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionLocalCancelUnknown", "Stopped waiting for the previous Linkly session locally, so the final result is still unknown. Ask a supervisor to confirm Linkly before charging again."),
                DialogDetails: BuildDialogDetails(status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(persistedAttempt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover active-session failed sessionId={LogValue(status?.SessionId)} error={ex.GetType().Name}",
                RecoveryLogContext(status?.SessionId),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionUnknown", "The previous Linkly session cannot be confirmed. Ask a supervisor to check Linkly before charging again."),
                DialogDetails: BuildDialogDetails(status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(persistedAttempt));
        }

        if (!IsFinal(status))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Checking,
                T("cardRecovery.linkly.activeSessionStillPending", "The previous Linkly session is still pending. Try recovery again or ask a supervisor to check Linkly."),
                DialogDetails: BuildDialogDetails(status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(persistedAttempt));
        }

        if (IsApproved(status))
        {
            if (!await TrySaveActiveSessionOutcomeAsync(
                    persistedAttempt,
                    LocalCardPaymentAttemptStatus.Approved,
                    status,
                    cancellationToken))
            {
                return BuildUnresolvedActiveSessionResult(
                    persistedAttempt,
                    T("payment.card.resultUnknown", "The card result is unknown. Do not collect payment again until recovery is completed."));
            }

            // 付款页恢复只确认并清理上一笔 active session，不能把结果合并进当前购物车。
            if (!await TryAcknowledgeActiveSessionAsync(settings, status, persistedAttempt, cancellationToken))
            {
                return ActiveSessionAcknowledgeFailed(status, persistedAttempt);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionApproved,
                T("cardRecovery.linkly.activeSessionApprovedCleared", "The previous Linkly transaction was successful and has been cleared. Continue the current order."),
                DialogDetails: BuildDialogDetails(status),
                BankReceipt: BuildActiveSessionBankReceipt(status, LinklyBankReceiptKind.RecoveredApproved));
        }

        if (IsDeclinedOrFailed(status))
        {
            if (!await TrySaveActiveSessionOutcomeAsync(
                    persistedAttempt,
                    MapFailureStatus(status),
                    status,
                    cancellationToken))
            {
                return BuildUnresolvedActiveSessionResult(
                    persistedAttempt,
                    T("payment.card.resultUnknown", "The card result is unknown. Do not collect payment again until recovery is completed."));
            }

            // 失败/未提交终态已可安全清理，收银员继续当前订单并按需重新刷卡。
            if (!await TryAcknowledgeActiveSessionAsync(settings, status, persistedAttempt, cancellationToken))
            {
                return ActiveSessionAcknowledgeFailed(status, persistedAttempt);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionNotPaid,
                T("cardRecovery.linkly.activeSessionNotPaidCleared", "The previous Linkly transaction was not paid successfully and has been cleared. Continue the current order and retry payment if needed."),
                DialogDetails: BuildDialogDetails(status),
                BankReceipt: BuildActiveSessionBankReceipt(status, LinklyBankReceiptKind.RecoveredFailed));
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.linkly.activeSessionUnknown", "The previous Linkly session cannot be confirmed. Ask a supervisor to check Linkly before charging again."),
            DialogDetails: BuildDialogDetails(status),
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(persistedAttempt));
    }

    private async Task<CardPaymentRecoveryResult> FinalizeSupervisorPaymentAsync(
        PosCartService cart,
        PosSessionState session,
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var confirmedPaid = string.Equals(
            attempt.ResponseCode,
            ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
            StringComparison.Ordinal);
        var confirmedNotPaid = string.Equals(
            attempt.ResponseCode,
            ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
            StringComparison.Ordinal);
        if (!confirmedPaid && !confirmedNotPaid)
        {
            return BuildUnresolvedActiveSessionResult(
                attempt,
                T("cardRecovery.linkly.activeSessionUnknown", "The previous Linkly session cannot be confirmed. Ask a supervisor to check Linkly before charging again."));
        }

        var draft = TryDeserializeDraft(attempt);
        var mode = ResolveAttemptConnectionMode(
            attempt,
            CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode));
        if (confirmedNotPaid)
        {
            if (draft is null && HasInvalidDraftPayload(attempt))
            {
                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."));
            }

            PosCartRecoveryPublicationResult? publication = null;
            if (draft is not null)
            {
                try
                {
                    publication = cart.TryPublishRecoverySnapshot(
                        new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid),
                        cart.Revision,
                        draft.CartSnapshot);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    TryWriteRecoveryLog(
                        $"supervisor not-paid cart publication failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                        ex,
                        attempt.AttemptGuid,
                        error: false);
                    return BuildUnresolvedActiveSessionResult(
                        attempt,
                        T("cardRecovery.linkly.currentCartNotEmpty", "The previous card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order."));
                }

                if (!publication.Value.Succeeded)
                {
                    return BuildUnresolvedActiveSessionResult(
                        attempt,
                        T("cardRecovery.linkly.currentCartNotEmpty", "The previous card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order."));
                }
            }

            if (!await CompleteSupervisorAcknowledgeAsync(settings, attempt, mode, cancellationToken))
            {
                if (publication is not null)
                {
                    // 终端/本地确认未完成时只撤回本 attempt 发布的购物车，主管决定继续保留。
                    cart.RollbackRecoveryPublication(
                        new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid));
                }

                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not clear it with Linkly. Try recovery again or ask a supervisor before charging again."));
            }

            if (draft is not null)
            {
                var markerPersisted = true;
                try
                {
                    // 中文注释：终端 acknowledge 成功后只补充本地 marker；草稿交接前不得终态化。
                    markerPersisted = await TryPersistAcknowledgedMarkerAsync(
                        attempt.AttemptGuid,
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    markerPersisted = false;
                    TryWriteRecoveryLog(
                        $"supervisor not-paid acknowledge marker failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                        ex,
                        attempt.AttemptGuid,
                        error: false);
                }

                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.DraftRestored,
                    T("cardRecovery.linkly.supervisorNotPaidRestored", "The bank confirmed that no payment was processed. The original order has been restored and can be paid again."),
                    TenderedAmount: draft.CurrentTenders.Sum(tender => tender.Amount),
                    DialogDetails: BuildDialogDetails(attempt),
                    RestoredTenders: draft.CurrentTenders,
                    HasPostCommitWarning: publication?.NotificationWarning == true || !markerPersisted)
                {
                    DraftHandoffKey = new CardRecoveryAttemptKey(
                        CardProcessorKind.Linkly,
                        attempt.AttemptGuid)
                };
            }

            // 无草稿的 ActiveSessionNotPaid 仍可在 acknowledge 后即时终态化。
            var finalizedAt = DateTimeOffset.UtcNow;
            var finalized = false;
            try
            {
                finalized = await RunLocalStoreAsync(
                    () => attemptRepository.TryFinalizeSupervisorNotPaidAndAcknowledgeAsync(
                        attempt.AttemptGuid,
                        attempt.Status,
                        attempt.UpdatedAt,
                        finalizedAt,
                        CancellationToken.None),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                TryWriteRecoveryLog(
                    $"supervisor not-paid atomic finalization failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: true);
            }

            if (!finalized)
            {
                if (publication is not null)
                {
                    cart.RollbackRecoveryPublication(
                        new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid));
                }

                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not finalize it locally. Run recovery again before charging again."));
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionNotPaid,
                T("cardRecovery.linkly.activeSessionNotPaidCleared", "The previous Linkly transaction was not paid successfully and has been cleared. Continue the current order and retry payment if needed."),
                DialogDetails: BuildDialogDetails(attempt),
                HasPostCommitWarning: false);
        }

        if (draft is null)
        {
            if (!cart.IsEmpty)
            {
                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("cardRecovery.linkly.currentCartNotEmpty", "The previous card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order."));
            }

            if (!await CompleteSupervisorAcknowledgeAsync(settings, attempt, mode, cancellationToken))
            {
                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not clear it with Linkly. Try recovery again or ask a supervisor before charging again."));
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.supervisorPaidNoDraft", "The supervisor confirmed the previous payment. The session is cleared, but no local order draft was available; reconcile the order before continuing."),
                DialogDetails: BuildDialogDetails(attempt),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        CardPaymentRecoveryResult result;
        if (mode is LinklyConnectionMode.LocalIp or LinklyConnectionMode.CloudDirectSync)
        {
            var authorization = new PaymentAuthorizationResult(
                true,
                Reference: attempt.PaymentReference ?? attempt.TxnRef ?? attempt.AttemptGuid.ToString("N"),
                Message: attempt.ResponseText,
                AuthorizedAmount: Math.Abs(draft.CardAmount),
                Processor: CardProcessorKind.Linkly.ToString(),
                Environment: attempt.Environment,
                ConnectionMode: attempt.ConnectionMode,
                TxnType: attempt.TxnType,
                SessionId: attempt.SessionId,
                TxnRef: attempt.TxnRef,
                ResponseCode: "00",
                ResponseText: attempt.ResponseText);
            result = await CompleteApprovedLocalAttemptAsync(
                cart,
                session,
                attempt,
                draft,
                authorization,
                cancellationToken);
            if (result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted)
            {
                result = await AddLocalSupervisorAcknowledgeWarningAsync(
                    attempt,
                    result,
                    cancellationToken);
            }
        }
        else
        {
            result = await CompleteApprovedAttemptAsync(
                cart,
                session,
                settings,
                attempt,
                draft,
                BuildSupervisorApprovedStatus(attempt),
                cancellationToken);
        }

        return result.Outcome is CardPaymentRecoveryOutcome.Unknown or CardPaymentRecoveryOutcome.Checking
            ? result with { PaymentSupervisorDetails = BuildPaymentSupervisorDetails(attempt) }
            : result;
    }

    public async Task<CardPaymentRecoveryResult> ManuallyClearActiveSessionAsync(
        string sessionId,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var normalizedSessionId = NormalizeOptional(sessionId);
        if (normalizedSessionId is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionManualClearMissing", "Cannot clear the previous Linkly session because the session id is missing."));
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Linkly ||
            !CanRecoverBackendActiveSession(settings))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionManualClearFailed", "POS could not clear the previous Linkly session. Try recovery again or check Linkly before charging again."));
        }

        try
        {
            // 人工核实后手动清除同样是主管决定，服务端据此把仍非终态的会话记为已结案。
            await backendTerminalClient.AcknowledgeSupervisorResolvedSessionAsync(settings, normalizedSessionId, cancellationToken);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionManuallyCleared,
                T("cardRecovery.linkly.activeSessionManuallyCleared", "The previous Linkly session was manually checked and cleared. Continue the current order."),
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    normalizedSessionId,
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.Now));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover active-session manual-clear failed sessionId={LogValue(normalizedSessionId)} error={ex.GetType().Name}",
                RecoveryLogContext(normalizedSessionId),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionManualClearFailed", "POS could not clear the previous Linkly session. Try recovery again or check Linkly before charging again."),
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    normalizedSessionId,
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.Now));
        }
    }

    private CardPaymentRecoveryResult ActiveSessionAcknowledgeFailed(
        LinklyCloudBackendSessionResponse status,
        LocalCardPaymentAttempt? attempt)
    {
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not clear it with Linkly. Try recovery again or ask a supervisor before charging again."),
            DialogDetails: BuildDialogDetails(status),
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
    }

    private async Task<CardPaymentRecoveryResult> CompleteSupervisorConfirmedRefundAsync(
        PosCartService cart,
        PosSessionState currentSession,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var dialogDetails = BuildDialogDetails(attempt);
        CardPaymentOrderDraft draft;
        try
        {
            draft = DeserializeDraft(attempt);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"confirmed refund draft invalid attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but POS could not rebuild the original return. Do not refund again; contact support."),
                DialogDetails: dialogDetails);
        }

        if (string.IsNullOrWhiteSpace(draft.OriginalReference))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but POS could not rebuild the original return. Do not refund again; contact support."),
                DialogDetails: dialogDetails);
        }

        LocalOrder? existingOrder;
        try
        {
            // 中文注释：部分退款/FinalizePending 续跑先核对已保存订单，证据不匹配时不能发布活动购物车。
            existingOrder = await RunLocalStoreAsync(
                () => orderRepository.GetOrderAsync(draft.OrderGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"confirmed refund existing-order query failed attemptGuid={attempt.AttemptGuid} orderGuid={draft.OrderGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: dialogDetails);
        }

        if (existingOrder is not null)
        {
            if (!HasExactAttemptTender(existingOrder, attempt.AttemptGuid))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but the saved order does not contain matching payment evidence. Do not refund again; contact support."),
                    DialogDetails: dialogDetails);
            }

            return await CompleteFinalizePendingExistingOrderAsync(
                attempt,
                LocalCardPaymentAttemptStatus.OrderCompleted,
                existingOrder,
                cancellationToken,
                T("cardRecovery.refund.confirmedCompleted", "The confirmed card refund was recovered and the return was saved."));
        }

        // 完整退款使用独立购物车完成订单，绝不触碰当前收银员正在编辑的购物车。
        var recoveryCart = new PosCartService();
        recoveryCart.RestoreSnapshot(draft.CartSnapshot);
        var tenderReference = CardRefundReference.Format(attempt.PaymentReference, draft.OriginalReference);
        var cardTender = new PaymentTender(
            PaymentMethodKind.Card,
            -Math.Abs(draft.CardAmount),
            tenderReference,
            IdempotencyKey: $"CARD_ATTEMPT:{attempt.AttemptGuid:N}");
        var tenders = draft.CurrentTenders.Concat([cardTender]).ToList();
        if (IsApprovedTenderPartial(draft, tenders))
        {
            PosCartRecoveryPublicationResult publication;
            try
            {
                // 部分退款必须把原退货草稿实际发布到空的活动购物车；仅返回 tenders 不算恢复成功。
                publication = cart.TryPublishRecoverySnapshot(
                    new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid),
                    cart.Revision,
                    draft.CartSnapshot);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                TryWriteRecoveryLog(
                    $"confirmed partial refund publication failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: false);
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but POS could not rebuild the original return. Do not refund again; contact support."),
                    DialogDetails: dialogDetails);
            }

            if (!publication.Succeeded)
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.linkly.currentCartNotEmpty", "The confirmed refund is saved, but the current cart is not empty. Complete or clear it, then run recovery again."),
                    DialogDetails: dialogDetails);
            }

            // 中文注释：部分退款交给 CashPaymentWorkflow 保存订单后的 CAS 收尾；此处必须保留 owner，
            // 由后续 CAS 成功释放，失败则由工作流回滚本 attempt 的发布。
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                T("cardRecovery.refund.confirmedTenderRestored", "The confirmed card refund was restored. Complete the remaining refund methods without refunding this card again."),
                TenderedAmount: tenders.Sum(tender => tender.Amount),
                DialogDetails: dialogDetails,
                RestoredTenders: tenders,
                HasPostCommitWarning: publication.NotificationWarning);
        }

        PaymentCheckoutResult checkoutResult;
        try
        {
            var cashTenderedAmount = tenders
                .Where(tender => tender.Method == PaymentMethodKind.Cash)
                .Sum(tender => tender.Amount);
            checkoutResult = checkout.CreatePaymentOrder(recoveryCart, draft.Session, tenders, cashTenderedAmount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"confirmed refund checkout rebuild failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but POS could not rebuild the original return. Do not refund again; contact support."),
                DialogDetails: dialogDetails);
        }

        var order = checkoutResult.Order with { OrderGuid = draft.OrderGuid };
        try
        {
            if (existingOrder is null)
            {
                // 仅新建订单时解析取单来源（与 LocalOrder 同一事务写入来源/完成 claim）；
                // 订单已存在（订单已保存、attempt 未收尾）时直接走既有订单幂等收尾。
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
            else
            {
                order = existingOrder;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"confirmed refund order save failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: true);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: dialogDetails);
        }

        var finalizedFullRefund = await FinalizeRecoveryOutcomeAsync(
            attempt,
            attempt.Status,
            attempt.UpdatedAt,
            LocalCardPaymentAttemptStatus.OrderCompleted,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var recoveredSession = currentSession;
        var hasPostCommitWarning = !finalizedFullRefund;
        try
        {
            var pendingSyncCount = await RunLocalStoreAsync(
                () => syncQueueRepository.CountPendingAsync(CancellationToken.None),
                CancellationToken.None);
            recoveredSession = currentSession with { PendingSyncCount = pendingSyncCount };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            TryWriteRecoveryLog(
                $"confirmed refund sync count refresh failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
        }
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            T("cardRecovery.refund.confirmedCompleted", "The confirmed card refund was recovered and the return was saved."),
            order,
            tenders.Sum(tender => tender.Amount),
            checkoutResult.ChangeAmount,
            recoveredSession,
            dialogDetails,
            HasPostCommitWarning: hasPostCommitWarning);
    }

    private Task<CardPaymentRecoveryResult> RestoreSupervisorApprovedRetryAsync(
        PosCartService cart,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var draft = TryDeserializeDraft(attempt);
        if (draft is null)
        {
            return Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: BuildDialogDetails(attempt)));
        }
        var publication = cart.TryPublishRecoverySnapshot(
            new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid),
            cart.Revision,
            draft.CartSnapshot);
        if (!publication.Succeeded)
        {
            return Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.currentCartNotEmpty", "The bank confirmed that no refund was processed, but the current cart is not empty. Complete or clear it, then run recovery again."),
                DialogDetails: BuildDialogDetails(attempt),
                HasPostCommitWarning: publication.NotificationWarning));
        }

        // 中文注释：ConfirmedNotRefunded 仍是可回滚的退款恢复草稿；UI 原子投影前不能
        // 完成 Abandoned CAS 或释放 owner，失败时 Presenter 才能按精确 attempt 回滚。
        return Task.FromResult(new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            T("cardRecovery.refund.retryAllowed", "The bank confirmed that no refund was processed. The original return is ready to retry with the same operation."),
            TenderedAmount: draft.CurrentTenders.Sum(tender => tender.Amount),
            DialogDetails: BuildDialogDetails(attempt),
            RestoredTenders: draft.CurrentTenders,
            HasPostCommitWarning: publication.NotificationWarning)
        {
            DraftHandoffKey = new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                attempt.AttemptGuid)
        });
    }

    /// <summary>
    /// UI 已完整接收 Linkly 草稿后，按精确金融结论 CAS 终结旧 attempt 并释放 publication。
    /// 在此之前必须保持 FinalizePending 与精确 provider owner。
    /// </summary>
    internal async Task<bool> CompleteDraftHandoffAsync(
        Guid attemptGuid,
        PosCartService cart,
        CancellationToken cancellationToken = default)
    {
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid);
        LocalCardPaymentAttempt? attempt;
        try
        {
            attempt = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"linkly draft handoff read failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                ex,
                attemptGuid,
                error: false);
            return false;
        }

        var isRefundHandoff = IsConfirmedNotRefundedDraftHandoff(attempt);
        var isSaleHandoff = IsSaleDraftHandoff(attempt);
        if (!isRefundHandoff && !isSaleHandoff)
        {
            return false;
        }

        var current = attempt!;
        var isTerminal = isRefundHandoff
            ? IsCompletedDraftHandoff(current)
            : IsCompletedSaleDraftHandoff(current);
        if (cart.RecoveryOwnerAttemptKey is not CardRecoveryAttemptKey ownerKey)
        {
            return isTerminal;
        }

        if (ownerKey != attemptKey)
        {
            return false;
        }

        if (!isTerminal)
        {
            if (isRefundHandoff)
            {
                if (current.Status != LocalCardPaymentAttemptStatus.Pending ||
                    !string.Equals(current.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                    !string.Equals(
                        current.RecoveryTargetStatus,
                        LocalCardPaymentAttemptStatus.Abandoned.ToString(),
                        StringComparison.Ordinal) ||
                    !await FinalizeRecoveryOutcomeAsync(
                        current,
                        current.Status,
                        current.UpdatedAt,
                        LocalCardPaymentAttemptStatus.Abandoned,
                        DateTimeOffset.UtcNow,
                        cancellationToken))
                {
                    LocalCardPaymentAttempt? winner;
                    try
                    {
                        winner = await RunLocalStoreAsync(
                            () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                            CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        TryWriteRecoveryLog(
                            $"linkly draft handoff winner read failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                            ex,
                            attemptGuid,
                            error: false);
                        return false;
                    }

                    if (!IsCompletedConfirmedNotRefundedDraftHandoff(winner))
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (!TryGetSaleDraftHandoffTarget(current, out var recoveryTarget) ||
                    !await FinalizeRecoveryOutcomeAsync(
                        current,
                        current.Status,
                        current.UpdatedAt,
                        recoveryTarget,
                        DateTimeOffset.UtcNow,
                        cancellationToken))
                {
                    LocalCardPaymentAttempt? winner;
                    try
                    {
                        winner = await RunLocalStoreAsync(
                            () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                            CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        TryWriteRecoveryLog(
                            $"linkly sale draft handoff winner read failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                            ex,
                            attemptGuid,
                            error: false);
                        return false;
                    }

                    if (!IsCompletedSaleDraftHandoff(winner))
                    {
                        return false;
                    }
                }
            }
        }

        LocalCardPaymentAttempt? persistedWinner;
        try
        {
            persistedWinner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"linkly draft handoff verification failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                ex,
                attemptGuid,
                error: false);
            return false;
        }

        if (isRefundHandoff
                ? !IsCompletedConfirmedNotRefundedDraftHandoff(persistedWinner)
                : !IsCompletedSaleDraftHandoff(persistedWinner))
        {
            return false;
        }

        // 数据库终态确认后才释放精确 owner；其它 attempt 的 publication 永远不能被本次交接触碰。
        return cart.CompleteRecoveryPublication(attemptKey);
    }

    private static bool IsConfirmedNotRefundedDraftHandoff(LocalCardPaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            attempt.ResponseCode,
            CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
            StringComparison.Ordinal);

    private static bool IsCompletedConfirmedNotRefundedDraftHandoff(LocalCardPaymentAttempt? attempt) =>
        IsConfirmedNotRefundedDraftHandoff(attempt) &&
        attempt is not null &&
        IsCompletedDraftHandoff(attempt);

    private static bool IsCompletedDraftHandoff(LocalCardPaymentAttempt attempt) =>
        attempt.Status == LocalCardPaymentAttemptStatus.Abandoned &&
        string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) &&
        attempt.RecoveryTargetStatus is null;

    private static bool IsSaleDraftHandoff(LocalCardPaymentAttempt? attempt) =>
        IsCompletedSaleDraftHandoff(attempt) ||
        TryGetSaleDraftHandoffTarget(attempt, out _);

    private static bool TryGetSaleDraftHandoffTarget(
        LocalCardPaymentAttempt? attempt,
        out LocalCardPaymentAttemptStatus recoveryTarget)
    {
        recoveryTarget = default;
        if (!IsLinklySaleAttempt(attempt) ||
            attempt is null ||
            !string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
            !Enum.TryParse(attempt.RecoveryTargetStatus, ignoreCase: false, out recoveryTarget))
        {
            return false;
        }

        if (recoveryTarget == LocalCardPaymentAttemptStatus.Abandoned)
        {
            return string.Equals(
                       attempt.ResponseCode,
                       ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
                       StringComparison.Ordinal) &&
                HasRequiredLinklyAcknowledgeEvidence(attempt);
        }

        return IsSaleFailureTarget(recoveryTarget) &&
            HasSaleFailureFinancialEvidence(attempt) &&
            HasRequiredLinklyAcknowledgeEvidence(attempt);
    }

    private static bool IsCompletedSaleDraftHandoff(LocalCardPaymentAttempt? attempt)
    {
        if (!IsLinklySaleAttempt(attempt) ||
            attempt is null ||
            !string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) ||
            attempt.RecoveryTargetStatus is not null ||
            !HasRequiredLinklyAcknowledgeEvidence(attempt))
        {
            return false;
        }

        if (string.Equals(
                attempt.ResponseCode,
                ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
                StringComparison.Ordinal))
        {
            return attempt.Status == LocalCardPaymentAttemptStatus.Abandoned;
        }

        return IsSaleFailureTarget(attempt.Status) &&
            HasSaleFailureFinancialEvidence(attempt);
    }

    private static bool IsLinklySaleAttempt(LocalCardPaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.Processor, CardProcessorKind.Linkly.ToString(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(attempt.OperationKind, "Sale", StringComparison.Ordinal);

    private static bool HasRequiredLinklyAcknowledgeEvidence(LocalCardPaymentAttempt attempt) =>
        string.IsNullOrWhiteSpace(attempt.SessionId) || attempt.AcknowledgedAt is not null;

    private static bool HasSaleFailureFinancialEvidence(LocalCardPaymentAttempt attempt) =>
        attempt.Status != LocalCardPaymentAttemptStatus.Approved &&
        !LinklyApprovalResponseCodes.IsApproved(attempt.ResponseCode) &&
        !IsSupervisorResolutionCode(attempt.ResponseCode) &&
        !string.Equals(
            attempt.ResponseCode,
            CardRefundSupervisorResolutionCodes.ConfirmedRefunded,
            StringComparison.Ordinal) &&
        !string.Equals(
            attempt.ResponseCode,
            CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
            StringComparison.Ordinal);

    private static bool IsSupervisorResolutionCode(string? responseCode) =>
        string.Equals(responseCode, ActiveSessionSupervisorResolutionCodes.ConfirmedPaid, StringComparison.Ordinal) ||
        string.Equals(responseCode, ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid, StringComparison.Ordinal) ||
        string.Equals(responseCode, ActiveSessionSupervisorResolutionCodes.ContinueWaiting, StringComparison.Ordinal);

    private static bool IsSaleFailureTarget(LocalCardPaymentAttemptStatus status) =>
        status is
            LocalCardPaymentAttemptStatus.Declined or
            LocalCardPaymentAttemptStatus.TimedOut or
            LocalCardPaymentAttemptStatus.Cancelled or
            LocalCardPaymentAttemptStatus.Failed;

    private static CardRefundRecoveryDetails BuildRefundDetails(
        LocalCardPaymentAttempt attempt,
        CardProcessorKind processor)
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
            processor,
            attempt.OperationGuid,
            attempt.Amount,
            originalReference);
    }

    private static string BuildSupervisorRetryTxnRef(
        LinklyConnectionMode connectionMode,
        string? previousTxnRef,
        string? originalReference)
    {
        var normalizedPreviousTxnRef = NormalizeLinklyReference(previousTxnRef);
        var normalizedOriginalReference = NormalizeLinklyReference(originalReference);
        string txnRef;
        do
        {
            txnRef = connectionMode == LinklyConnectionMode.LocalIp
                ? LinklyLocalTxnRef.Create('R', Guid.NewGuid().ToString("D"))
                : Guid.NewGuid().ToString("N");
        }
        while (string.Equals(txnRef, normalizedPreviousTxnRef, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(txnRef, normalizedOriginalReference, StringComparison.OrdinalIgnoreCase));

        return txnRef;
    }

    private async Task<CardPaymentRecoveryResult> CompleteApprovedAttemptAsync(
        PosCartService cart,
        PosSessionState currentSession,
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        CardPaymentOrderDraft draft,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        var paymentReference = BuildPaymentReference(attempt, status);
        // 金融批准证据必须先落库。后续草稿反序列化、金额计算或订单重建失败时，
        // 只能保留 Approved 待恢复，绝不能把已确认付款降级为 RequiresReview。
        if (!await TryPersistApprovedOutcomeAsync(
                attempt,
                status.ResponseCode,
                status.ResponseText,
                paymentReference))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("payment.card.resultUnknown", "The card result is unknown. Do not collect payment again until recovery is completed."),
                DialogDetails: BuildDialogDetails(attempt, status),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        PosCartService recoveryCart;
        decimal tenderAmount;
        try
        {
            recoveryCart = new PosCartService();
            recoveryCart.RestoreSnapshot(draft.CartSnapshot);
            if (string.IsNullOrWhiteSpace(draft.TxnType))
            {
                throw new InvalidOperationException("Approved payment draft is missing TxnType.");
            }

            tenderAmount = draft.TxnType.Equals("R", StringComparison.OrdinalIgnoreCase)
                ? -Math.Abs(draft.CardAmount)
                : Math.Abs(draft.CardAmount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return await MarkApprovedRecoveryRequiresReviewAsync(
                attempt,
                status.ResponseCode,
                status.ResponseText,
                paymentReference,
                BuildDialogDetails(attempt, status),
                ex,
                cancellationToken);
        }

        var cardTender = new PaymentTender(
            PaymentMethodKind.Card,
            tenderAmount,
            paymentReference,
            CardTransactions: [BuildCardTransaction(attempt, status, tenderAmount)],
            IdempotencyKey: $"CARD_ATTEMPT:{attempt.AttemptGuid:N}");
        var tenders = draft.CurrentTenders.Concat([cardTender]).ToList();
        var cashTenderedAmount = tenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount);
        if (IsApprovedTenderPartial(draft, tenders))
        {
            return await RestoreApprovedTenderAsync(
                cart,
                draft,
                attempt,
                status.ResponseCode,
                status.ResponseText,
                cardTender.Reference,
                tenders,
                BuildDialogDetails(attempt, status),
                cancellationToken);
        }

        PaymentCheckoutResult checkoutResult;
        try
        {
            checkoutResult = checkout.CreatePaymentOrder(recoveryCart, draft.Session, tenders, cashTenderedAmount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return await MarkApprovedRecoveryRequiresReviewAsync(
                attempt,
                status.ResponseCode,
                status.ResponseText,
                cardTender.Reference,
                BuildDialogDetails(attempt, status),
                ex,
                cancellationToken);
        }

        var order = checkoutResult.Order with { OrderGuid = draft.OrderGuid };
        try
        {
            var existingOrder = await RunLocalStoreAsync(
                () => orderRepository.GetOrderAsync(draft.OrderGuid, CancellationToken.None),
                CancellationToken.None);
            if (existingOrder is null)
            {
                // 仅新建订单时解析取单来源；订单已保存时直接走既有订单幂等收尾。
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
            else
            {
                // 仅精确 Card attempt key 能证明该订单已承接本次扣款；同 GUID 的其他订单证据不能终态化当前 attempt。
                if (!HasExactAttemptTender(existingOrder, attempt.AttemptGuid))
                {
                    return new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but the saved order does not contain matching payment evidence. Ask a supervisor to reconcile it before continuing."),
                        DialogDetails: BuildDialogDetails(attempt, status),
                        PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
                }

                order = existingOrder;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return await MarkApprovedRecoveryRequiresReviewAsync(
                attempt,
                status.ResponseCode,
                status.ResponseText,
                cardTender.Reference,
                BuildDialogDetails(attempt, status),
                ex,
                cancellationToken);
        }

        var hasPostCommitWarning = false;
        try
        {
            if (!await CompleteApprovedFinalizationAsync(attempt, CancellationToken.None))
            {
                hasPostCommitWarning = true;
            }
            else if (!await TryAcknowledgeAsync(
                         settings,
                         attempt,
                         status.SessionId,
                         status.TxnRef,
                         CancellationToken.None))
            {
                hasPostCommitWarning = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            ConsoleLog.WriteError(
                "CardRecovery",
                $"approved order saved but attempt finalization failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
        }

        var pendingSyncCount = currentSession.PendingSyncCount;
        try
        {
            pendingSyncCount = await RunLocalStoreAsync(
                () => syncQueueRepository.CountPendingAsync(CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"approved order saved but pending sync refresh failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
        }
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            string.Empty,
            order,
            tenders.Sum(tender => tender.Amount),
            checkoutResult.ChangeAmount,
            currentSession with { PendingSyncCount = pendingSyncCount },
            BuildDialogDetails(attempt, status),
            HasPostCommitWarning: hasPostCommitWarning);
    }

    private async Task<CardPaymentRecoveryResult> CompleteApprovedLocalAttemptAsync(
        PosCartService cart,
        PosSessionState currentSession,
        LocalCardPaymentAttempt attempt,
        CardPaymentOrderDraft draft,
        PaymentAuthorizationResult authorization,
        CancellationToken cancellationToken)
    {
        var responseTransaction = authorization.CardTransactions?.FirstOrDefault();
        var responseCode = responseTransaction?.ResponseCode ?? authorization.ResponseCode;
        var responseText = responseTransaction?.ResponseText ?? authorization.ResponseText;
        var paymentReference = BuildLocalPaymentReference(attempt, authorization);
        // 与 CloudBackend 路径一致：先保全批准证据，再尝试物化草稿。
        if (!await TryPersistApprovedOutcomeAsync(
                attempt,
                responseCode,
                responseText,
                paymentReference))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("payment.card.resultUnknown", "The card result is unknown. Do not collect payment again until recovery is completed."),
                DialogDetails: BuildDialogDetails(attempt, authorization),
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        PosCartService recoveryCart;
        decimal tenderAmount;
        IReadOnlyList<CardTransactionDto> cardTransactions;
        try
        {
            recoveryCart = new PosCartService();
            recoveryCart.RestoreSnapshot(draft.CartSnapshot);
            if (string.IsNullOrWhiteSpace(draft.TxnType))
            {
                throw new InvalidOperationException("Approved payment draft is missing TxnType.");
            }

            tenderAmount = draft.TxnType.Equals("R", StringComparison.OrdinalIgnoreCase)
                ? -Math.Abs(draft.CardAmount)
                : Math.Abs(draft.CardAmount);
            cardTransactions = BuildLocalCardTransactions(attempt, authorization, tenderAmount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return await MarkApprovedRecoveryRequiresReviewAsync(
                attempt,
                responseCode,
                responseText,
                paymentReference,
                BuildDialogDetails(attempt, authorization),
                ex,
                cancellationToken);
        }

        var cardTender = new PaymentTender(
            PaymentMethodKind.Card,
            tenderAmount,
            paymentReference,
            CardTransactions: cardTransactions,
            IdempotencyKey: $"CARD_ATTEMPT:{attempt.AttemptGuid:N}");
        var tenders = draft.CurrentTenders.Concat([cardTender]).ToList();
        var cashTenderedAmount = tenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount);
        if (IsApprovedTenderPartial(draft, tenders))
        {
            return await RestoreApprovedTenderAsync(
                cart,
                draft,
                attempt,
                responseCode,
                responseText,
                cardTender.Reference,
                tenders,
                BuildDialogDetails(attempt, authorization),
                cancellationToken);
        }

        PaymentCheckoutResult checkoutResult;
        try
        {
            checkoutResult = checkout.CreatePaymentOrder(recoveryCart, draft.Session, tenders, cashTenderedAmount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return await MarkApprovedRecoveryRequiresReviewAsync(
                attempt,
                responseCode,
                responseText,
                cardTender.Reference,
                BuildDialogDetails(attempt, authorization),
                ex,
                cancellationToken);
        }

        var order = checkoutResult.Order with { OrderGuid = draft.OrderGuid };
        try
        {
            var existingOrder = await RunLocalStoreAsync(
                () => orderRepository.GetOrderAsync(draft.OrderGuid, CancellationToken.None),
                CancellationToken.None);
            if (existingOrder is null)
            {
                // 仅新建订单时解析取单来源；订单已保存时直接走既有订单幂等收尾。
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
            else
            {
                // Local IP 与 Cloud 使用同一金融证据门禁，避免错误复用同 OrderGuid 的其他订单。
                if (!HasExactAttemptTender(existingOrder, attempt.AttemptGuid))
                {
                    return new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but the saved order does not contain matching payment evidence. Ask a supervisor to reconcile it before continuing."),
                        DialogDetails: BuildDialogDetails(attempt, authorization),
                        PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
                }

                order = existingOrder;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return await MarkApprovedRecoveryRequiresReviewAsync(
                attempt,
                responseCode,
                responseText,
                cardTender.Reference,
                BuildDialogDetails(attempt, authorization),
                ex,
                cancellationToken);
        }

        var hasPostCommitWarning = false;
        try
        {
            hasPostCommitWarning = !await CompleteApprovedFinalizationAsync(
                attempt,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            ConsoleLog.WriteError(
                "CardRecovery",
                $"approved local order saved but attempt finalization failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
        }

        var pendingSyncCount = currentSession.PendingSyncCount;
        try
        {
            pendingSyncCount = await RunLocalStoreAsync(
                () => syncQueueRepository.CountPendingAsync(CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"approved local order saved but pending sync refresh failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
        }
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            string.Empty,
            order,
            tenders.Sum(tender => tender.Amount),
            checkoutResult.ChangeAmount,
            currentSession with { PendingSyncCount = pendingSyncCount },
            BuildDialogDetails(attempt, authorization),
            HasPostCommitWarning: hasPostCommitWarning);
    }

    private async Task<bool> PersistRecoveryOutcomeAsync(
        LocalCardPaymentAttempt attempt,
        LocalCardPaymentAttemptStatus openStatus,
        string? responseCode,
        string? responseText,
        string? paymentReference,
        LocalCardPaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalCardPaymentAttemptStatus recoveryTargetStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunLocalStoreAsync(
                () => attemptRepository.TryPersistRecoveryOutcomeAsync(
                    attempt.AttemptGuid,
                    openStatus,
                    responseCode,
                    responseText,
                    paymentReference,
                    expectedStatus,
                    expectedUpdatedAt,
                    recoveryTargetStatus,
                    updatedAt,
                    CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "CardRecovery",
                $"recover persist recovery outcome failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return false;
        }
    }

    private async Task<CardPaymentRecoveryResult> PersistInvalidRecoveredDraftAsync(
        LocalCardPaymentAttempt attempt,
        LocalCardPaymentAttemptStatus recoveredStatus,
        string? responseCode,
        string? responseText,
        string? paymentReference,
        CardPaymentRecoveryDialogDetails dialogDetails,
        CancellationToken cancellationToken)
    {
        var persisted = recoveredStatus == LocalCardPaymentAttemptStatus.OrderCompleted
            ? await TryPersistApprovedOutcomeAsync(
                attempt,
                responseCode,
                responseText,
                paymentReference)
            : await PersistRecoveryOutcomeAsync(
                attempt,
                attempt.Status,
                responseCode,
                responseText,
                paymentReference,
                attempt.Status,
                attempt.UpdatedAt,
                recoveredStatus,
                DateTimeOffset.UtcNow,
                cancellationToken);
        if (!persisted)
        {
            TryWriteRecoveryLog(
                $"recover invalid draft financial result persistence failed attemptGuid={attempt.AttemptGuid} status={recoveredStatus}");
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card result was confirmed, but POS could not safely rebuild the order. Ask a supervisor to reconcile it before continuing."),
            DialogDetails: dialogDetails,
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
    }

    private async Task<bool> FinalizeRecoveryOutcomeAsync(
        LocalCardPaymentAttempt attempt,
        LocalCardPaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalCardPaymentAttemptStatus recoveryTargetStatus,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunLocalStoreAsync(
                () => attemptRepository.TryFinalizeRecoveryOutcomeAsync(
                    attempt.AttemptGuid,
                    expectedStatus,
                    expectedUpdatedAt,
                    recoveryTargetStatus,
                    completedAt,
                    CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "CardRecovery",
                $"recover finalize recovery outcome failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return false;
        }
    }

    private async Task<CardPaymentRecoveryResult> FinalizeDeclinedOrFailedAsync(
        PosCartService cart,
        LocalCardPaymentAttempt attempt,
        CardPaymentOrderDraft draft,
        LocalCardPaymentAttemptStatus failureStatus,
        string? responseCode,
        string? responseText,
        string? paymentReference,
        CardPaymentRecoveryDialogDetails dialogDetails,
        string reason,
        Func<Task<bool>>? acknowledgeAsync,
        CancellationToken cancellationToken)
    {
        // 先持久化失败金融结果并标记 FinalizePending，再发布草稿，避免“先恢复后落库”的不一致。
        var current = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
            CancellationToken.None) ?? attempt;
        var openStatus = current.Status;
        DateTimeOffset persistedAt;
        if (string.Equals(
                current.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            if (!Enum.TryParse<LocalCardPaymentAttemptStatus>(
                    current.RecoveryTargetStatus,
                    ignoreCase: false,
                    out var pendingTarget) ||
                pendingTarget != failureStatus)
            {
                return await ReconcileRecoveryConflictAsync(attempt, dialogDetails, cancellationToken);
            }

            // 崩溃/最终 CAS 失败后的续跑：金融结果已持久化，直接重新发布并等待 handoff，不能再次覆盖证据。
            persistedAt = current.UpdatedAt;
            responseCode = current.ResponseCode;
            responseText = current.ResponseText;
            paymentReference = current.PaymentReference;
        }
        else
        {
            var expectedUpdatedAt = current.UpdatedAt;
            persistedAt = DateTimeOffset.UtcNow;
            if (!await PersistRecoveryOutcomeAsync(
                    attempt,
                    openStatus,
                    responseCode,
                    responseText,
                    paymentReference,
                    openStatus,
                    expectedUpdatedAt,
                    failureStatus,
                    persistedAt,
                    cancellationToken))
            {
                return await ReconcileRecoveryConflictAsync(attempt, dialogDetails, cancellationToken);
            }
        }

        var publication = cart.TryPublishRecoverySnapshot(
            new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid),
            cart.Revision,
            draft.CartSnapshot);
        if (!publication.Succeeded)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.currentCartNotEmpty", "The previous card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order."),
                DialogDetails: dialogDetails,
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt),
                HasPostCommitWarning: publication.NotificationWarning);
        }

        if (acknowledgeAsync is not null && !await acknowledgeAsync())
        {
            // Linkly 尚未确认清理时，不能把恢复草稿暴露给收银员，也不能把本地 attempt 终态化。
            cart.RollbackRecoveryPublication(
                new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid));
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not clear it with Linkly. Try recovery again before taking another payment."),
                DialogDetails: dialogDetails,
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        var markerPersisted = true;
        if (acknowledgeAsync is not null)
        {
            try
            {
                markerPersisted = await TryPersistAcknowledgedMarkerAsync(
                    attempt.AttemptGuid,
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                markerPersisted = false;
                TryWriteRecoveryLog(
                    $"declined recovery acknowledge marker failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: false);
            }
        }

        var hasWarning = publication.NotificationWarning || !markerPersisted;

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            Format("cardRecovery.linkly.failed", "The previous card payment failed: {0}. The order has been restored. Select a payment method again.", reason),
            TenderedAmount: draft.CurrentTenders.Sum(tender => tender.Amount),
            DialogDetails: dialogDetails,
            RestoredTenders: draft.CurrentTenders,
            HasPostCommitWarning: hasWarning)
        {
            DraftHandoffKey = new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                attempt.AttemptGuid)
        };
    }

    private async Task<CardPaymentRecoveryResult> ReconcileRecoveryConflictAsync(
        LocalCardPaymentAttempt attempt,
        CardPaymentRecoveryDialogDetails dialogDetails,
        CancellationToken cancellationToken)
    {
        var winner = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
            CancellationToken.None);
        if (winner is null)
        {
            return CardPaymentRecoveryResult.None;
        }

        if (IsSupervisorResolvedPayment(winner))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                DialogDetails: dialogDetails,
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(winner));
        }

        if (IsTerminalRecoveryStatus(winner))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.None,
                T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
                DialogDetails: dialogDetails);
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.linkly.unknown", "The previous card result cannot be confirmed. Ask a supervisor to confirm the Linkly backend status before continuing."),
            DialogDetails: dialogDetails,
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(winner));
    }

    private async Task<bool> TryPersistApprovedOutcomeAsync(
        LocalCardPaymentAttempt attempt,
        string? responseCode,
        string? responseText,
        string? paymentReference)
    {
        if (attempt.Status == LocalCardPaymentAttemptStatus.OrderCompleted)
        {
            return true;
        }

        if (string.Equals(
                attempt.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            return string.Equals(
                       attempt.RecoveryTargetStatus,
                       LocalCardPaymentAttemptStatus.OrderCompleted.ToString(),
                       StringComparison.Ordinal) ||
                string.Equals(
                    attempt.RecoveryTargetStatus,
                    LocalCardPaymentAttemptStatus.Approved.ToString(),
                    StringComparison.Ordinal);
        }

        if (IsSupervisorResolvedPayment(attempt))
        {
            return true;
        }

        try
        {
            var persistedAt = DateTimeOffset.UtcNow;
            var persisted = await RunLocalStoreAsync(
                () => attemptRepository.TryPersistRecoveryOutcomeAsync(
                    attempt.AttemptGuid,
                    LocalCardPaymentAttemptStatus.Approved,
                    responseCode,
                    responseText,
                    paymentReference,
                    attempt.Status,
                    attempt.UpdatedAt,
                    LocalCardPaymentAttemptStatus.OrderCompleted,
                    persistedAt,
                    CancellationToken.None),
                CancellationToken.None);
            if (persisted)
            {
                return true;
            }

            var winner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
            return winner?.Status == LocalCardPaymentAttemptStatus.OrderCompleted ||
                (winner is not null &&
                 string.Equals(winner.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) &&
                 string.Equals(
                     winner.RecoveryTargetStatus,
                     LocalCardPaymentAttemptStatus.OrderCompleted.ToString(),
                     StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"approved outcome persistence failed before order save attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: true);
            return false;
        }
    }

    private async Task<bool> CompleteApprovedFinalizationAsync(
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var current = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
            CancellationToken.None) ?? attempt;
        if (current.Status == LocalCardPaymentAttemptStatus.OrderCompleted &&
            !string.Equals(
                current.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            return true;
        }

        return await FinalizeRecoveryOutcomeAsync(
            current,
            current.Status,
            current.UpdatedAt,
            LocalCardPaymentAttemptStatus.OrderCompleted,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private async Task<LocalCardPaymentAttempt?> PrepareApprovedTenderFinalizationAsync(
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var current = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
            CancellationToken.None) ?? attempt;
        if (string.Equals(current.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) &&
            string.Equals(
                current.RecoveryTargetStatus,
                LocalCardPaymentAttemptStatus.OrderCompleted.ToString(),
                StringComparison.Ordinal))
        {
            return current;
        }

        // 中文注释：旧版本可能把部分已批准付款错误改成 Approved 目标；这里只把它恢复为
        // 订单落盘后才能完成的 OrderCompleted，绝不能在 UI 投影前终态化或释放 owner。
        if (!string.Equals(current.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
            !string.Equals(
                current.RecoveryTargetStatus,
                LocalCardPaymentAttemptStatus.Approved.ToString(),
                StringComparison.Ordinal))
        {
            return null;
        }

        var updatedAt = DateTimeOffset.UtcNow;
        var retargeted = await RunLocalStoreAsync(
            () => attemptRepository.TryRetargetRecoveryFinalizationAsync(
                current.AttemptGuid,
                current.Status,
                current.UpdatedAt,
                LocalCardPaymentAttemptStatus.Approved,
                LocalCardPaymentAttemptStatus.OrderCompleted,
                updatedAt,
                CancellationToken.None),
            cancellationToken);
        if (retargeted)
        {
            return current with
            {
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString(),
                UpdatedAt = updatedAt
            };
        }

        var winner = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(current.AttemptGuid, CancellationToken.None),
            CancellationToken.None);
        return winner is not null &&
            string.Equals(winner.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) &&
            string.Equals(
                winner.RecoveryTargetStatus,
                LocalCardPaymentAttemptStatus.OrderCompleted.ToString(),
                StringComparison.Ordinal)
            ? winner
            : null;
    }

    private async Task<CardPaymentRecoveryResult> RestoreApprovedTenderAsync(
        PosCartService cart,
        CardPaymentOrderDraft draft,
        LocalCardPaymentAttempt attempt,
        string? responseCode,
        string? responseText,
        string? paymentReference,
        IReadOnlyList<PaymentTender> tenders,
        CardPaymentRecoveryDialogDetails dialogDetails,
        CancellationToken cancellationToken)
    {
        if (!IsSupervisorResolvedPayment(attempt))
        {
            if (!await TryPersistApprovedOutcomeAsync(
                    attempt,
                    responseCode,
                    responseText,
                    paymentReference))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely rebuild the order. Ask a supervisor to confirm the payment before continuing."),
                    DialogDetails: dialogDetails,
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
            }
        }

        var finalizePending = await PrepareApprovedTenderFinalizationAsync(
            attempt,
            cancellationToken);
        if (finalizePending is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely rebuild the order. Ask a supervisor to confirm the payment before continuing."),
                DialogDetails: dialogDetails,
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        attempt = finalizePending;

        PosCartRecoveryPublicationResult publication;
        try
        {
            // 终端已批准但未覆盖整单时，以 attempt 所有权一次性发布购物车和 tender。
            publication = cart.TryPublishRecoverySnapshot(
                new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid),
                cart.Revision,
                draft.CartSnapshot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"recover approved tender publication failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely rebuild the order. Ask a supervisor to confirm the payment before continuing."),
                DialogDetails: dialogDetails,
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        if (!publication.Succeeded)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.currentCartNotEmpty", "The previous card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order."),
                DialogDetails: dialogDetails,
                PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
        }

        // 中文注释：publication 只代表草稿已准备好；必须等 UI 原子投影和本地订单保存后，
        // CashPaymentWorkflowService 才能完成 OrderCompleted CAS 并释放精确 owner。
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            T("cardRecovery.linkly.approvedTenderRestored", "The previous card payment was approved and restored as a tender. Complete the remaining payment amount before finishing the order."),
            TenderedAmount: tenders.Sum(tender => tender.Amount),
            DialogDetails: dialogDetails,
            RestoredTenders: tenders,
            HasPostCommitWarning: publication.NotificationWarning);
    }

    private Task<CardPaymentRecoveryResult> MarkApprovedRecoveryRequiresReviewAsync(
        LocalCardPaymentAttempt attempt,
        string? responseCode,
        string? responseText,
        string? paymentReference,
        CardPaymentRecoveryDialogDetails dialogDetails,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _ = responseCode;
        _ = responseText;
        _ = paymentReference;
        // Approved 已是不可覆盖的金融事实；订单重建失败只记录诊断并保持开放，
        // 不能把状态降级成 RequiresReview，也不能让日志订阅者替换恢复结果。
        TryWriteRecoveryLog(
            $"recover approved draft rebuild failed attemptGuid={attempt.AttemptGuid} error={exception.GetType().Name} message={exception.Message}",
            exception,
            attempt.AttemptGuid,
            error: true);

        return Task.FromResult(new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely rebuild the order. Ask a supervisor to confirm the payment before continuing."),
            DialogDetails: dialogDetails,
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt)));
    }

    private static void TryWriteRecoveryLog(
        string message,
        Exception? exception = null,
        Guid? attemptGuid = null,
        bool error = false)
    {
        try
        {
            // 带异常的失败记 Warning（订单/金额需人工处理时记 Error），并以 attemptGuid 作 TraceId；其余保持 Information。
            var context = RecoveryLogContext(attemptGuid?.ToString("D"));
            if (exception is null)
            {
                ConsoleLog.Write("CardRecovery", message);
            }
            else if (error)
            {
                ConsoleLog.WriteError("CardRecovery", message, context, exception);
            }
            else
            {
                ConsoleLog.WriteWarning("CardRecovery", message, context, exception);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 日志订阅者属于提交后的尽力通知，不能覆盖已确定的金融结果。
        }
    }

    private static bool IsApprovedTenderPartial(CardPaymentOrderDraft draft, IReadOnlyList<PaymentTender> tenders)
    {
        var actualAmount = RoundCurrency(draft.ActualAmount);
        var tenderTotal = RoundCurrency(tenders.Sum(tender => tender.Amount));

        if (actualAmount > 0m)
        {
            return tenderTotal > 0m && tenderTotal < actualAmount;
        }

        if (actualAmount < 0m)
        {
            return tenderTotal < 0m && tenderTotal > actualAmount;
        }

        return false;
    }

    private static decimal RoundCurrency(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static CardPaymentRecoveryDialogDetails BuildDialogDetails(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse? status = null)
    {
        return new CardPaymentRecoveryDialogDetails(
            NormalizeOptional(status?.SessionId) ?? NormalizeOptional(attempt.SessionId),
            NormalizeOptional(status?.TxnRef) ?? NormalizeOptional(attempt.TxnRef),
            status?.ResponseCode,
            status?.ResponseText,
            attempt.Amount,
            DateTimeOffset.Now);
    }

    private static CardPaymentRecoveryDialogDetails BuildDialogDetails(
        LocalCardPaymentAttempt attempt,
        PaymentAuthorizationResult authorization)
    {
        var transaction = authorization.CardTransactions?.FirstOrDefault();
        return new CardPaymentRecoveryDialogDetails(
            NormalizeOptional(authorization.SessionId) ?? NormalizeOptional(attempt.SessionId),
            ResolveAuthorizationTxnRef(authorization) ?? NormalizeOptional(attempt.TxnRef),
            transaction?.ResponseCode ?? authorization.ResponseCode,
            transaction?.ResponseText ?? authorization.ResponseText,
            authorization.AuthorizedAmount ?? attempt.Amount,
            DateTimeOffset.Now);
    }

    private static CardPaymentRecoveryDialogDetails? BuildDialogDetails(LinklyCloudBackendSessionResponse? status)
    {
        if (status is null)
        {
            return null;
        }

        return new CardPaymentRecoveryDialogDetails(
            NormalizeOptional(status.SessionId),
            NormalizeOptional(status.TxnRef),
            status.ResponseCode,
            status.ResponseText,
            null,
            DateTimeOffset.Now);
    }

    private static CardPaymentRecoveryBankReceipt? BuildActiveSessionBankReceipt(
        LinklyCloudBackendSessionResponse status,
        LinklyBankReceiptKind kind)
    {
        // 认证恢复证据优先使用后端汇总 ReceiptText，缺失时回退到 receipt notification。
        var receiptText = ReadReceiptText(status);
        if (receiptText is null)
        {
            return null;
        }

        return new CardPaymentRecoveryBankReceipt(
            status.Environment,
            status.SessionId,
            receiptText,
            kind,
            status.ResponseCode,
            status.ResponseText);
    }

    private static string BuildLocalPaymentReference(
        LocalCardPaymentAttempt attempt,
        PaymentAuthorizationResult authorization)
    {
        return NormalizeOptional(authorization.Reference) ??
            $"ANZ:{ResolveAuthorizationTxnRef(authorization) ?? NormalizeOptional(attempt.TxnRef) ?? attempt.AttemptGuid.ToString("N")}";
    }

    private static IReadOnlyList<CardTransactionDto> BuildLocalCardTransactions(
        LocalCardPaymentAttempt attempt,
        PaymentAuthorizationResult authorization,
        decimal amount)
    {
        if (authorization.CardTransactions is { Count: > 0 } transactions)
        {
            return transactions;
        }

        var txnRef = ResolveAuthorizationTxnRef(authorization) ??
            NormalizeOptional(attempt.TxnRef) ??
            attempt.AttemptGuid.ToString("N");
        return
        [
            new CardTransactionDto(
                "ANZ",
                txnRef,
                null,
                null,
                null,
                null,
                null,
                authorization.ResponseCode,
                authorization.ResponseText ?? authorization.Message,
                null,
                DateTimeOffset.UtcNow,
                Math.Abs(amount),
                null)
        ];
    }

    private async Task RetryCompletedAttemptAcknowledgeAsync(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        ConsoleLog.Write(
            "CardRecovery",
            $"recover acknowledge retry attemptGuid={attempt.AttemptGuid} sessionId={LogValue(attempt.SessionId)} txnRef={LogValue(attempt.TxnRef)}");
        await TryAcknowledgeAsync(settings, attempt, attempt.SessionId!, attempt.TxnRef, cancellationToken);
    }

    private static bool IsFinalFailureAwaitingAcknowledgement(LocalCardPaymentAttempt attempt) =>
        string.Equals(attempt.Processor, nameof(CardProcessorKind.Linkly), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(attempt.ConnectionMode, nameof(LinklyConnectionMode.CloudBackendAsync), StringComparison.OrdinalIgnoreCase) &&
        attempt.OperationKind is "Sale" or "Refund" &&
        attempt.Status is LocalCardPaymentAttemptStatus.Declined or LocalCardPaymentAttemptStatus.Cancelled or
            LocalCardPaymentAttemptStatus.Failed or LocalCardPaymentAttemptStatus.TimedOut &&
        attempt.CompletedAt is not null &&
        attempt.AcknowledgedAt is null &&
        !string.IsNullOrWhiteSpace(attempt.SessionId) &&
        attempt.RecoveryPhase == CardRecoveryPhases.None &&
        !(attempt.ResponseCode?.StartsWith("SUPERVISOR_", StringComparison.Ordinal) ?? false);

    private async Task<CardPaymentRecoveryResult> RetryFinalFailureAcknowledgementAsync(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        // 失败事实已完成本地提交；只释放原 session，不重新查询、扣款/退款或发布购物车草稿。
        var acknowledged = await TryAcknowledgeAsync(
            settings, attempt, attempt.SessionId!, attempt.TxnRef, cancellationToken);
        LogRecoveryResult(settings, attempt, null,
            acknowledged ? CardPaymentRecoveryOutcome.None : CardPaymentRecoveryOutcome.Unknown,
            acknowledged ? "final-failure-acknowledged" : "final-failure-ack-pending");
        return acknowledged
            ? CardPaymentRecoveryResult.None
            : new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not clear it with Linkly. Try recovery again or ask a supervisor before charging again."),
                DialogDetails: BuildDialogDetails(attempt));
    }

    private async Task<bool> TryPersistAcknowledgedMarkerAsync(
        Guid attemptGuid,
        CancellationToken cancellationToken)
    {
        var current = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
            CancellationToken.None);
        if (current is null)
        {
            return false;
        }

        if (current.AcknowledgedAt is not null)
        {
            return true;
        }

        return await RunLocalStoreAsync(
            () => attemptRepository.TryMarkAcknowledgedAsync(
                current.AttemptGuid,
                current.Status,
                current.UpdatedAt,
                DateTimeOffset.UtcNow,
                CancellationToken.None),
            cancellationToken);
    }

    private async Task<bool> TryAcknowledgeAsync(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        string sessionId,
        string? txnRef,
        CancellationToken cancellationToken)
    {
        try
        {
            await AcknowledgeBackendSessionAsync(settings, attempt, sessionId, cancellationToken);
            return await TryPersistAcknowledgedMarkerAsync(attempt.AttemptGuid, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            // 鏈湴璁㈠崟/鑽夌鎭㈠宸茬粡瀹屾垚锛宎ck 澶辫触鍙奖鍝?backend 娓呯悊锛屼笉鑳介樆鏂惎鍔ㄤ綋楠屻€?
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover acknowledge failed attemptGuid={attempt.AttemptGuid} sessionId={LogValue(sessionId)} txnRef={LogValue(txnRef)} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return false;
        }
    }

    private Task AcknowledgeBackendSessionAsync(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        string sessionId,
        CancellationToken cancellationToken)
    {
        // 主管结案码已随 attempt 落库，重启续跑或补发 ack 时同样能识别；自动恢复确认的终态走普通 ack。
        return IsSupervisorResolvedPayment(attempt)
            ? backendTerminalClient.AcknowledgeSupervisorResolvedSessionAsync(settings, sessionId, cancellationToken)
            : backendTerminalClient.AcknowledgeSessionAsync(settings, sessionId, cancellationToken);
    }

    private async Task<bool> TryAcknowledgeActiveSessionAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        LocalCardPaymentAttempt? attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await backendTerminalClient.AcknowledgeSessionAsync(settings, status.SessionId, cancellationToken);
            if (attempt is not null)
            {
                return await TryPersistAcknowledgedMarkerAsync(
                    attempt.AttemptGuid,
                    CancellationToken.None);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recover active-session acknowledge failed sessionId={LogValue(status.SessionId)} txnRef={LogValue(status.TxnRef)} error={ex.GetType().Name}",
                RecoveryLogContext(status.SessionId),
                ex);
            return false;
        }
    }

    private async Task<LocalCardPaymentAttempt> PersistActiveSessionAsync(
        CardTerminalSettings settings,
        PosSessionState session,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(status.SessionId))
        {
            throw new InvalidOperationException("Linkly active session does not contain a SessionId.");
        }

        var now = DateTimeOffset.UtcNow;
        var attempt = new LocalCardPaymentAttempt(
            Guid.NewGuid(),
            status.SessionId.Trim(),
            NormalizeOptional(status.TxnRef),
            CardProcessorKind.Linkly.ToString(),
            settings.Environment.ToString(),
            nameof(LinklyConnectionMode.CloudBackendAsync),
            "P",
            0m,
            LocalCardPaymentAttemptStatus.Recovering,
            "{}",
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            status.ResponseCode,
            status.ResponseText,
            null,
            now,
            now,
            null,
            null,
            OperationKind: "ActiveSession",
            OperationGuid: Guid.NewGuid());
        return await RunLocalStoreAsync(
            () => attemptRepository.CreateOrGetActiveSessionAsync(attempt, CancellationToken.None),
            CancellationToken.None);
    }

    private async Task<bool> TrySaveActiveSessionOutcomeAsync(
        LocalCardPaymentAttempt? attempt,
        LocalCardPaymentAttemptStatus outcome,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        if (attempt is null)
        {
            return false;
        }

        try
        {
            return await RunLocalStoreAsync(
                () => attemptRepository.TryUpdateOutcomeAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    outcome,
                    status.ResponseCode,
                    status.ResponseText,
                    NormalizeOptional(status.TxnRef) ?? attempt.PaymentReference,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "CardRecovery",
                $"active-session outcome persistence failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return false;
        }
    }

    private async Task<bool> CompleteSupervisorAcknowledgeAsync(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt,
        LinklyConnectionMode mode,
        CancellationToken cancellationToken)
    {
        // 中文注释：本地 marker 只在 acknowledge 成功后写入；重启恢复时不能再次触发同一终端 API。
        if (attempt.AcknowledgedAt is not null)
        {
            return true;
        }

        var sessionId = NormalizeOptional(attempt.SessionId);
        if (mode == LinklyConnectionMode.CloudBackendAsync && sessionId is not null)
        {
            try
            {
                await AcknowledgeBackendSessionAsync(
                    settings,
                    attempt,
                    sessionId,
                    cancellationToken);
                return true;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                TryWriteRecoveryLog(
                    $"supervisor payment backend acknowledge canceled without caller cancellation attemptGuid={attempt.AttemptGuid} sessionId={LogValue(sessionId)} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: false);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                TryWriteRecoveryLog(
                    $"supervisor payment backend acknowledge failed attemptGuid={attempt.AttemptGuid} sessionId={LogValue(sessionId)} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: false);
                return false;
            }
        }

        return true;
    }

    private async Task<CardPaymentRecoveryResult> AddLocalSupervisorAcknowledgeWarningAsync(
        LocalCardPaymentAttempt attempt,
        CardPaymentRecoveryResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TryPersistAcknowledgedMarkerAsync(
                    attempt.AttemptGuid,
                    CancellationToken.None)
                ? result
                : result with { HasPostCommitWarning = true };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"supervisor-approved local payment saved but acknowledge marker failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                RecoveryLogContext(attempt.AttemptGuid.ToString("D")),
                ex);
            return result with { HasPostCommitWarning = true };
        }
    }

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
            TryWriteRecoveryLog(
                $"persisted supervisor resolution recovery failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                ex,
                attemptGuid,
                error: false);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage());
        }
    }

    private async Task<bool> IsResolutionLockRetainedAsync(
        Guid attemptGuid,
        bool succeeded)
    {
        try
        {
            var current = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                CancellationToken.None);
            if (current is null ||
                string.Equals(
                    current.RecoveryPhase,
                    CardRecoveryPhases.FinalizePending,
                    StringComparison.Ordinal))
            {
                return true;
            }

            // Succeeded 代表是否恢复出订单/草稿；无草稿未付款也可以已安全终态化并释放锁。
            return !IsPersistedTerminalStatus(current.Status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"supervisor resolution lock check failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                ex,
                attemptGuid,
                error: false);
            return true;
        }
    }

    private static bool IsSupervisorResolvedPayment(LocalCardPaymentAttempt? attempt) =>
        attempt is not null &&
        (string.Equals(
             attempt.ResponseCode,
             ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
             StringComparison.Ordinal) ||
         string.Equals(
             attempt.ResponseCode,
             ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
             StringComparison.Ordinal));

    private static bool IsTerminalRecoveryStatus(LocalCardPaymentAttempt attempt)
    {
        var terminal = IsPersistedTerminalStatus(attempt.Status);
        if (!terminal)
        {
            return false;
        }

        // 主管决定与未 acknowledge 的已完成订单仍是可恢复状态，不能当作不可恢复终态拒绝。
        if (IsSupervisorResolvedPayment(attempt))
        {
            // 已完成 terminal acknowledge 的主管终态不得再次发布草稿或补写订单；
            // 只有尚未完成收尾的历史主管决定才继续进入恢复。
            return attempt.AcknowledgedAt is not null;
        }

        return !(attempt.Status == LocalCardPaymentAttemptStatus.OrderCompleted &&
            attempt.AcknowledgedAt is null &&
            !string.IsNullOrWhiteSpace(attempt.SessionId));
    }

    private static bool IsPersistedTerminalStatus(LocalCardPaymentAttemptStatus status) =>
        status is
            LocalCardPaymentAttemptStatus.Declined or
            LocalCardPaymentAttemptStatus.TimedOut or
            LocalCardPaymentAttemptStatus.Cancelled or
            LocalCardPaymentAttemptStatus.Failed or
            LocalCardPaymentAttemptStatus.OrderCompleted or
            LocalCardPaymentAttemptStatus.Abandoned;

    private static bool IsSupervisorResolvedActiveSession(LocalCardPaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "ActiveSession", StringComparison.Ordinal) &&
        IsSupervisorResolvedPayment(attempt);

    private static CardPaymentSupervisorDetails? BuildPaymentSupervisorDetails(
        LocalCardPaymentAttempt? attempt)
    {
        var sessionId = NormalizeOptional(attempt?.SessionId) ?? NormalizeOptional(attempt?.TxnRef);
        return attempt is null || sessionId is null
            ? null
            : new CardPaymentSupervisorDetails(
                attempt.AttemptGuid,
                CardProcessorKind.Linkly,
                sessionId,
                attempt.OperationGuid,
                attempt.Status,
                attempt.UpdatedAt);
    }

    private CardPaymentRecoveryResult BuildUnresolvedActiveSessionResult(
        LocalCardPaymentAttempt? attempt,
        string message)
    {
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            message,
            DialogDetails: attempt is null ? null : BuildDialogDetails(attempt),
            PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
    }

    private async Task<CardPaymentRecoveryResult> ReplayHistoricalSupervisorNotPaidAcknowledgementAsync(
        CardTerminalSettings settings,
        LinklyConnectionMode mode,
        LocalCardPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        // 中文注释：旧版本可能已经写入 Abandoned 但遗漏 acknowledge marker；重放只能补清理证据，
        // 不能再次执行终态 CAS，也不能覆盖已保存的金融字段。
        if (!await CompleteSupervisorAcknowledgeAsync(settings, attempt, mode, cancellationToken))
        {
            return BuildUnresolvedActiveSessionResult(
                attempt,
                T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not clear it with Linkly. Try recovery again or ask a supervisor before charging again."));
        }

        try
        {
            if (!await TryPersistAcknowledgedMarkerAsync(attempt.AttemptGuid, CancellationToken.None))
            {
                return BuildUnresolvedActiveSessionResult(
                    attempt,
                    T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not finalize its local acknowledge marker. Run recovery again before charging again."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"historical supervisor not-paid acknowledge marker failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return BuildUnresolvedActiveSessionResult(
                attempt,
                T("cardRecovery.linkly.activeSessionAcknowledgeFailed", "The previous Linkly result was confirmed, but POS could not finalize its local acknowledge marker. Run recovery again before charging again."));
        }

        return CardPaymentRecoveryResult.None;
    }

    private async Task<CardPaymentRecoveryResult> CompleteFinalizePendingExistingOrderAsync(
        LocalCardPaymentAttempt attempt,
        LocalCardPaymentAttemptStatus recoveryTargetStatus,
        LocalOrder existingOrder,
        CancellationToken cancellationToken,
        string? successMessage = null)
    {
        var finalized = await FinalizeRecoveryOutcomeAsync(
            attempt,
            attempt.Status,
            attempt.UpdatedAt,
            recoveryTargetStatus,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (!finalized)
        {
            LocalCardPaymentAttempt? winner;
            try
            {
                winner = await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                TryWriteRecoveryLog(
                    $"recover finalize-pending winner read failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: false);
                winner = null;
            }

            // 中文注释：CAS false 可能只是并发赢家已完成；只有读到相同目标终态、阶段清空且无待定目标才算成功。
            var winnerCompleted = winner is not null &&
                winner.Status == recoveryTargetStatus &&
                IsPersistedTerminalStatus(winner.Status) &&
                string.Equals(winner.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) &&
                winner.RecoveryTargetStatus is null;
            if (!winnerCompleted)
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.linkly.approvedRecoveryRequiresReview", "The previous card payment was approved, but POS could not safely finalize the saved order. Ask a supervisor to reconcile it before continuing."),
                    DialogDetails: BuildDialogDetails(attempt),
                    PaymentSupervisorDetails: BuildPaymentSupervisorDetails(attempt));
            }
        }

        return new CardPaymentRecoveryResult(
            recoveryTargetStatus == LocalCardPaymentAttemptStatus.OrderCompleted
                ? CardPaymentRecoveryOutcome.OrderCompleted
                : CardPaymentRecoveryOutcome.DraftRestored,
            successMessage ?? string.Empty,
            existingOrder,
            DialogDetails: BuildDialogDetails(attempt));
    }

    private static bool HasExactAttemptTender(LocalOrder order, Guid attemptGuid)
    {
        var expectedKey = $"CARD_ATTEMPT:{attemptGuid:N}";
        return order.Payments.Any(payment =>
            payment.Method == PaymentMethodKind.Card &&
            string.Equals(payment.IdempotencyKey, expectedKey, StringComparison.Ordinal));
    }

    private static bool IsHistoricalSupervisorNotPaidAwaitingAcknowledgement(
        LocalCardPaymentAttempt attempt) =>
        attempt.Status == LocalCardPaymentAttemptStatus.Abandoned &&
        attempt.AcknowledgedAt is null &&
        string.Equals(
            attempt.ResponseCode,
            ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
            StringComparison.Ordinal);

    private static bool HasInvalidDraftPayload(LocalCardPaymentAttempt attempt) =>
        !string.Equals(attempt.OperationKind, "ActiveSession", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(attempt.OrderDraftJson);

    private static CardPaymentOrderDraft? TryDeserializeDraft(LocalCardPaymentAttempt attempt)
    {
        if (string.Equals(attempt.OperationKind, "ActiveSession", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attempt.OrderDraftJson))
        {
            return null;
        }

        return CardRecoveryCartMaterializer.TryPrepare(
                   attempt.OrderDraftJson,
                   JsonOptions,
                   out var draft) &&
               draft is not null &&
               draft.OrderGuid != Guid.Empty
            ? draft
            : null;
    }

    private static LinklyCloudBackendSessionResponse BuildSupervisorApprovedStatus(
        LocalCardPaymentAttempt attempt)
    {
        return new LinklyCloudBackendSessionResponse(
            attempt.Environment,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.SessionId ?? attempt.TxnRef ?? attempt.AttemptGuid.ToString("N"),
            StatusCompleted,
            attempt.TxnRef,
            "00",
            attempt.ResponseText,
            null,
            null,
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            null,
            null,
            0,
            null,
            null,
            null,
            [],
            true);
    }

    private static LocalFinancialSupervisorResolution BuildPaymentSupervisorJournal(
        LocalCardPaymentAttempt attempt,
        string sessionKey,
        CardPaymentSupervisorResolution resolution,
        DateTimeOffset resolvedAt)
    {
        var resolutionGuid = Guid.NewGuid();
        var auditEventId = Guid.NewGuid();
        var auditEvent = new OperationAuditEventDto
        {
            EventId = auditEventId,
            OccurredAtUtc = resolvedAt,
            OperationType = "CARD_PAYMENT_SUPERVISOR_RESOLUTION",
            Outcome = "Succeeded",
            CashierId = resolution.OperatorCashierId,
            UserGuid = resolution.OperatorUserGuid,
            CashierName = resolution.OperatorName,
            StoreCode = attempt.StoreCode,
            DeviceCode = attempt.DeviceCode,
            CorrelationId = attempt.AttemptGuid.ToString("D"),
            PaymentMethod = attempt.Processor,
            ReasonCode = resolution.Decision.ToString(),
            SafeMessage = resolution.Reason,
            PaymentAmount = Math.Abs(attempt.Amount),
            Properties = new Dictionary<string, string?>
            {
                ["attemptGuid"] = attempt.AttemptGuid.ToString("D"),
                ["operationGuid"] = attempt.OperationGuid?.ToString("D"),
                ["sessionId"] = sessionKey,
                ["evidence"] = resolution.Evidence,
                ["financialReference"] = resolution.PaymentReference
            }
        };
        return new LocalFinancialSupervisorResolution(
            resolutionGuid,
            LocalFinancialSupervisorResolutionTarget.ActiveSession,
            attempt.Processor,
            attempt.Environment,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.AttemptGuid,
            null,
            attempt.OperationGuid,
            sessionKey,
            resolution.Decision.ToString(),
            resolution.OperatorCashierId,
            resolution.OperatorUserGuid,
            resolution.OperatorName,
            resolution.Reason,
            resolution.Evidence,
            resolution.PaymentReference,
            null,
            resolvedAt,
            auditEventId,
            JsonSerializer.Serialize(auditEvent, JsonOptions));
    }

    private static LocalFinancialSupervisorResolution BuildRefundSupervisorJournal(
        LocalCardPaymentAttempt attempt,
        CardRefundSupervisorResolution resolution,
        PosSessionState session,
        string? retryTxnRef,
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
            PaymentMethod = attempt.Processor,
            ReasonCode = resolution.Decision.ToString(),
            SafeMessage = resolution.Reason,
            PaymentAmount = Math.Abs(attempt.Amount),
            Properties = new Dictionary<string, string?>
            {
                ["attemptGuid"] = attempt.AttemptGuid.ToString("D"),
                ["operationGuid"] = attempt.OperationGuid?.ToString("D"),
                ["evidence"] = resolution.Evidence,
                ["financialReference"] = resolution.RefundReference,
                ["retryReference"] = retryTxnRef
            }
        };
        return new LocalFinancialSupervisorResolution(
            resolutionGuid,
            LocalFinancialSupervisorResolutionTarget.CardRefund,
            attempt.Processor,
            attempt.Environment,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.AttemptGuid,
            null,
            attempt.OperationGuid,
            attempt.SessionId,
            resolution.Decision.ToString(),
            operatorCashierId,
            operatorUserGuid,
            operatorName,
            resolution.Reason,
            resolution.Evidence,
            resolution.RefundReference,
            retryTxnRef,
            resolvedAt,
            auditEventId,
            JsonSerializer.Serialize(auditEvent, JsonOptions));
    }

    private async Task<LocalCardPaymentAttempt> BindRecoveredSessionAsync(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        if (!CanBindRecoveredSession(attempt, status))
        {
            return attempt;
        }

        var now = DateTimeOffset.UtcNow;
        var updated = await RunLocalStoreAsync(
            () => attemptRepository.TryUpdateSessionAsync(
                attempt.AttemptGuid,
                attempt.Status,
                attempt.UpdatedAt,
                status.SessionId,
                status.TxnRef,
                now,
                cancellationToken),
            cancellationToken);
        if (!updated)
        {
            return await RunLocalStoreAsync(
                       () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                       CancellationToken.None)
                   ?? attempt;
        }

        ConsoleLog.Write(
            "CardRecovery",
            $"recover session bound attemptGuid={attempt.AttemptGuid} sessionId={LogValue(status.SessionId)} txnRef={LogValue(status.TxnRef)} status={status.Status}");
        return attempt with
        {
            SessionId = status.SessionId,
            TxnRef = status.TxnRef ?? attempt.TxnRef,
            Status = LocalCardPaymentAttemptStatus.SessionStarted,
            UpdatedAt = now
        };
    }

    private static CardPaymentOrderDraft DeserializeDraft(LocalCardPaymentAttempt attempt)
    {
        if (!CardRecoveryCartMaterializer.TryPrepare(
                attempt.OrderDraftJson,
                JsonOptions,
                out var draft) ||
            draft is null ||
            draft.OrderGuid == Guid.Empty)
        {
            throw new InvalidOperationException("Card payment recovery draft is invalid.");
        }

        return draft;
    }

    private static bool IsFinal(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase) ||
            IsCancelledStatus(status.Status);
    }

    private static bool IsApproved(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
            status.TransactionSuccess == true;
    }

    private static bool IsDeclinedOrFailed(LinklyCloudBackendSessionResponse status)
    {
        if (string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase))
        {
            return status.TransactionSuccess == false;
        }

        return IsFinal(status);
    }

    private static bool IsCancelledStatus(string? status)
    {
        // Linkly 后端可能用英式或美式拼写表示收银员取消成功；都应视为可清除终态。
        return string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanBindRecoveredSession(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status)
    {
        if (!TextEquals(attempt.Environment, status.Environment) ||
            !TextEquals(attempt.StoreCode, status.StoreCode) ||
            !TextEquals(attempt.DeviceCode, status.DeviceCode))
        {
            return false;
        }

        var attemptSessionId = NormalizeOptional(attempt.SessionId);
        var statusSessionId = NormalizeOptional(status.SessionId);
        var attemptTxnRef = NormalizeOptional(attempt.TxnRef);
        var statusTxnRef = NormalizeOptional(status.TxnRef);

        if (attemptSessionId is not null && !TextEquals(attemptSessionId, statusSessionId))
        {
            return false;
        }

        if (attemptTxnRef is not null && statusTxnRef is not null && !TextEquals(attemptTxnRef, statusTxnRef))
        {
            return false;
        }

        if (attemptSessionId is not null && attemptTxnRef is null && statusTxnRef is not null)
        {
            return true;
        }

        return attemptTxnRef is not null &&
            attemptSessionId is null &&
            statusTxnRef is not null &&
            TextEquals(attemptTxnRef, statusTxnRef);
    }

    private static bool StatusMatchesAttempt(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status,
        bool statusFromResumable,
        out string mismatchReason)
    {
        if (!TextEquals(attempt.Environment, status.Environment))
        {
            mismatchReason = "environment-mismatch";
            return false;
        }

        var attemptSessionId = NormalizeOptional(attempt.SessionId);
        var statusSessionId = NormalizeOptional(status.SessionId);
        if (attemptSessionId is not null)
        {
            if (!TextEquals(attemptSessionId, statusSessionId))
            {
                mismatchReason = "session-id-mismatch";
                return false;
            }
        }
        else if (!statusFromResumable)
        {
            mismatchReason = "missing-attempt-session";
            return false;
        }

        var attemptTxnRef = NormalizeOptional(attempt.TxnRef);
        var statusTxnRef = NormalizeOptional(status.TxnRef);
        if (attemptTxnRef is not null)
        {
            if (statusTxnRef is null || !TextEquals(attemptTxnRef, statusTxnRef))
            {
                mismatchReason = "txn-ref-mismatch";
                return false;
            }
        }
        else if (attemptSessionId is null)
        {
            // 鏈湴娌℃湁 sessionId 鏃跺彧鑳介€氳繃 txnRef 缁戝畾 backend resumable锛岀己澶卞垯涓嶈兘鑷姩澶勭悊銆?
            mismatchReason = "missing-recoverable-binding";
            return false;
        }

        mismatchReason = string.Empty;
        return true;
    }

    private static LocalCardPaymentAttemptStatus MapFailureStatus(LinklyCloudBackendSessionResponse status)
    {
        var text = $"{status.Status} {status.ResponseText}".ToUpperInvariant();
        if (text.Contains("TIMEOUT", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.TimedOut;
        }

        if (text.Contains("CANCEL", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.Cancelled;
        }

        if (text.Contains("DECLIN", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.Declined;
        }

        return LocalCardPaymentAttemptStatus.Failed;
    }

    private static LinklyConnectionMode ResolveAttemptConnectionMode(
        LocalCardPaymentAttempt attempt,
        LinklyConnectionMode fallback)
    {
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(attempt.ConnectionMode, fallback);
        // 本地 LocalIp attempt 不会产生 backend session；若已有 SessionId，说明实际交易已进入 CloudBackendAsync。
        return mode == LinklyConnectionMode.LocalIp && !string.IsNullOrWhiteSpace(attempt.SessionId)
            ? LinklyConnectionMode.CloudBackendAsync
            : mode;
    }

    private static bool HasLocalFinalResult(PaymentAuthorizationResult authorization)
    {
        var transaction = authorization.CardTransactions?.FirstOrDefault();
        return !authorization.ResultUnknown &&
            (!string.IsNullOrWhiteSpace(authorization.Reference) ||
                !string.IsNullOrWhiteSpace(authorization.ResponseCode) ||
                !string.IsNullOrWhiteSpace(authorization.ResponseText) ||
                !string.IsNullOrWhiteSpace(transaction?.TxnRef) ||
                !string.IsNullOrWhiteSpace(transaction?.ResponseCode) ||
                !string.IsNullOrWhiteSpace(transaction?.ResponseText));
    }

    internal static bool LocalAuthorizationMatchesAttempt(
        LocalCardPaymentAttempt attempt,
        PaymentAuthorizationResult authorization)
    {
        if (ResolveAttemptConnectionMode(attempt, LinklyConnectionMode.LocalIp) == LinklyConnectionMode.CloudDirectSync &&
            authorization.Reference?.StartsWith("ANZCLOUD:", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!LinklyLocalTxnRef.TryNormalizeHistoricalReference(attempt.TxnRef, out var expectedTxnRef))
            {
                return false;
            }

            var refundReference = NormalizeOptional(authorization.CardTransactions?.FirstOrDefault()?.RefundReference);
            var expectedReference = refundReference is null
                ? $"ANZCLOUD:{expectedTxnRef}"
                : $"ANZCLOUD:{expectedTxnRef}:{refundReference}";
            if (!string.Equals(authorization.Reference, expectedReference, StringComparison.Ordinal))
            {
                return false;
            }

            // 直连付款引用包含渠道前缀及可选退款凭据；先完整核对，再仅对校验副本规范化。
            // 原响应仍用于落单，且下面继续验证所有 TxnRef、交易类型和金额，不能忽略冲突字段。
            authorization = authorization with { Reference = expectedTxnRef };
        }

        return LinklyLocalTransactionIdentity.Matches(
            attempt.TxnRef,
            attempt.TxnType,
            attempt.Amount,
            authorization);
    }

    private static LocalCardPaymentAttemptStatus MapLocalFailureStatus(PaymentAuthorizationResult authorization)
    {
        var transaction = authorization.CardTransactions?.FirstOrDefault();
        var responseCode = NormalizeOptional(transaction?.ResponseCode) ?? NormalizeOptional(authorization.ResponseCode);
        if (IsTimeoutResponseCode(responseCode))
        {
            return LocalCardPaymentAttemptStatus.TimedOut;
        }

        if (IsCancelResponseCode(responseCode))
        {
            return LocalCardPaymentAttemptStatus.Cancelled;
        }

        if (IsDeclineResponseCode(responseCode))
        {
            return LocalCardPaymentAttemptStatus.Declined;
        }

        var text = $"{authorization.Message} {authorization.ResponseText} {transaction?.ResponseText}".ToUpperInvariant();
        if (text.Contains("TIMEOUT", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.TimedOut;
        }

        if (text.Contains("CANCEL", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.Cancelled;
        }

        if (text.Contains("DECLIN", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.Declined;
        }

        return LocalCardPaymentAttemptStatus.Failed;
    }

    private static bool CanRecoverBackendActiveSession(CardTerminalSettings settings)
    {
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
        if (mode == LinklyConnectionMode.CloudBackendAsync)
        {
            return true;
        }

        return settings.LinklyConnectionModePriority is { Count: > 0 } priority &&
            CardTerminalSettings.NormalizeLinklyConnectionModePriority(priority, mode)
                .Contains(LinklyConnectionMode.CloudBackendAsync);
    }

    private static bool IsDeclineResponseCode(string? responseCode)
    {
        return !string.IsNullOrWhiteSpace(responseCode) &&
            !LinklyApprovalResponseCodes.IsApproved(responseCode) &&
            !IsCancelResponseCode(responseCode) &&
            !IsTimeoutResponseCode(responseCode);
    }

    private static bool IsCancelResponseCode(string? responseCode)
    {
        return string.Equals(responseCode, "C0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode, "CA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode, "CANCEL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode, "CANCELED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTimeoutResponseCode(string? responseCode)
    {
        return string.Equals(responseCode, "TO", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode, "TIMEOUT", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveAuthorizationTxnRef(PaymentAuthorizationResult authorization)
    {
        return NormalizeLinklyReference(authorization.TxnRef) ??
            NormalizeLinklyReference(authorization.CardTransactions?.FirstOrDefault()?.TxnRef) ??
            NormalizeLinklyReference(authorization.Reference);
    }

    private static string? NormalizeLinklyReference(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return null;
        }

        return normalized.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase)
            ? NormalizeOptional(normalized[4..])
            : normalized;
    }

    private string LegacyLinklyTxnRefMessage()
    {
        return T(
            "cardRecovery.linkly.legacyTxnRefRequiresReview",
            "The saved Linkly reference is an older value that does not meet the 16-character protocol. It cannot be matched safely. Do not charge or refund again. A supervisor must compare the terminal receipt, amount, time, device, RRN, STAN and authorization code.");
    }

    private static CardPaymentSupervisorDetails BuildLegacyPaymentSupervisorDetails(
        LocalCardPaymentAttempt attempt)
    {
        return BuildPaymentSupervisorDetails(attempt) ?? new CardPaymentSupervisorDetails(
            attempt.AttemptGuid,
            CardProcessorKind.Linkly,
            attempt.AttemptGuid.ToString("N"),
            attempt.OperationGuid,
            attempt.Status,
            attempt.UpdatedAt);
    }

    private static string BuildPaymentReference(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status)
    {
        if (!string.IsNullOrWhiteSpace(attempt.PaymentReference))
        {
            return attempt.PaymentReference;
        }

        var txnRef = status.TxnRef ?? attempt.TxnRef ?? status.SessionId;
        return LinklyBackendPaymentReference.Format(txnRef, status.SessionId, status.Environment, TryReadRefundReference(status));
    }

    private static string? TryReadRefundReference(LinklyCloudBackendSessionResponse status)
    {
        foreach (var notification in (status.Notifications ?? []).Reverse())
        {
            if (!string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(notification.PayloadJson))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(notification.PayloadJson);
                var response = ReadResponse(document.RootElement);
                var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
                // 官方 GET transaction payload 里 RFN 是后续退款和认证证据链的关键引用。
                return TryReadRefundReferenceValue(purchaseAnalysisData) ??
                    TryReadRefundReferenceValue(response) ??
                    TryReadRefundReferenceValue(document.RootElement);
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static string? ReadReceiptText(LinklyCloudBackendSessionResponse status)
    {
        return NormalizeOptional(status.ReceiptText) ?? ReadReceiptText(status.Notifications ?? []);
    }

    private static string? ReadReceiptText(IReadOnlyList<LinklyCloudBackendNotificationDto> notifications)
    {
        var receipts = notifications
            .Where(notification => string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase))
            .Select(notification => ReadReceiptNotification(notification.PayloadJson))
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt))
            .Select(receipt => receipt!)
            .ToArray();

        return receipts.Length == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, receipts);
    }

    private static string? ReadReceiptNotification(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return ReadReceiptText(document.RootElement) ?? ReadReceiptText(ReadResponse(document.RootElement));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadReceiptText(JsonElement element)
    {
        if (!TryGetProperty(element, "ReceiptText", out var receipt))
        {
            return null;
        }

        return receipt.ValueKind == JsonValueKind.String
            ? NormalizeOptional(receipt.GetString())
            : null;
    }

    private static JsonElement ReadResponse(JsonElement root)
    {
        return TryGetProperty(root, "Response", out var response) ? response : root;
    }

    private static JsonElement ReadValue(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value) ? value : default;
    }

    private static string? TryReadRefundReferenceValue(JsonElement element, bool allowScalar = false)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => TryReadRefundReferenceObject(element),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => TryReadRefundReferenceValue(item))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            JsonValueKind.String when allowScalar => NormalizeOptional(element.GetString()),
            _ => null
        };
    }

    private static string? TryReadRefundReferenceObject(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "RFN", StringComparison.OrdinalIgnoreCase))
            {
                return TryReadRefundReferenceValue(property.Value, allowScalar: true);
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            var value = TryReadRefundReferenceValue(property.Value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static CardTransactionDto BuildCardTransaction(
        LocalCardPaymentAttempt attempt,
        LinklyCloudBackendSessionResponse status,
        decimal amount)
    {
        if (status.CardTransaction is not null)
        {
            var transactionResult = LinklyBackendTerminalClient.ReadTransactionResult(
                status,
                Math.Abs(amount),
                attempt.TxnRef ?? string.Empty);
            return LinklyBackendTerminalClient.ToCardTransaction(
                transactionResult,
                transactionResult.Amount,
                status.ReceiptText);
        }

        return new CardTransactionDto(
            "ANZ",
            status.TxnRef ?? attempt.TxnRef ?? status.SessionId,
            null,
            null,
            null,
            null,
            null,
            status.ResponseCode,
            status.ResponseText,
            null,
            DateTimeOffset.UtcNow,
            Math.Abs(amount),
            status.ReceiptText);
    }

    /// <summary>
    /// 恢复流程失败日志的上下文：以 attemptGuid（无则 sessionId）作 TraceId，便于与支付/终端日志串联。
    /// </summary>
    private static ApplicationLogContext RecoveryLogContext(string? traceId)
    {
        return new ApplicationLogContext(TraceId: string.IsNullOrWhiteSpace(traceId) ? null : traceId);
    }

    private static void LogRecoveryScan(
        CardTerminalSettings settings,
        PosSessionState session,
        LocalCardPaymentAttempt? attempt)
    {
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "startup-recovery",
            "scan",
            environment: settings.Environment,
            sessionId: NormalizeOptional(attempt?.SessionId),
            success: attempt is not null,
            reason: attempt is null ? "no-open-attempt" : null,
            details: new
            {
                timestamp = DateTimeOffset.Now,
                pendingAttemptFound = attempt is not null,
                storeCode = session.StoreCode,
                deviceCode = session.DeviceCode,
                cashierId = session.CashierId,
                requestedCashierId = (string?)null,
                attemptCashierId = attempt?.CashierId,
                selectedEnvironment = settings.Environment.ToString(),
                certCase = "4.1.1",
                transactionReference = NormalizeOptional(attempt?.SessionId) ?? NormalizeOptional(attempt?.TxnRef),
                attemptGuid = attempt?.AttemptGuid,
                localStatus = attempt?.Status.ToString(),
                attemptSessionId = NormalizeOptional(attempt?.SessionId),
                txnRef = NormalizeOptional(attempt?.TxnRef),
                txnType = attempt?.TxnType,
                amount = attempt?.Amount,
                createdAt = attempt?.CreatedAt,
                updatedAt = attempt?.UpdatedAt
            },
            // 启动扫描没找到待恢复记录是常态，不能因 success=false 被自动升为 Warning。
            level: LinklyLogLevel.Information);
    }

    private static void LogRecoveryMarkedRecovering(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt attempt)
    {
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "startup-recovery",
            "marked-recovering",
            environment: settings.Environment,
            sessionId: NormalizeOptional(attempt.SessionId),
            details: new
            {
                timestamp = DateTimeOffset.Now,
                attemptGuid = attempt.AttemptGuid,
                certCase = "4.1.2",
                transactionReference = NormalizeOptional(attempt.SessionId) ?? NormalizeOptional(attempt.TxnRef),
                localStatus = attempt.Status.ToString(),
                attemptSessionId = NormalizeOptional(attempt.SessionId),
                txnRef = NormalizeOptional(attempt.TxnRef),
                txnType = attempt.TxnType,
                amount = attempt.Amount,
                storeCode = attempt.StoreCode,
                deviceCode = attempt.DeviceCode,
                cashierId = attempt.CashierId
            });
    }

    private static void LogRecoveryResult(
        CardTerminalSettings settings,
        LocalCardPaymentAttempt? attempt,
        LinklyCloudBackendSessionResponse? status,
        CardPaymentRecoveryOutcome outcome,
        string reason,
        string? error = null)
    {
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "startup-recovery",
            "result",
            environment: settings.Environment,
            sessionId: NormalizeOptional(attempt?.SessionId) ?? NormalizeOptional(status?.SessionId),
            success: outcome is CardPaymentRecoveryOutcome.OrderCompleted or CardPaymentRecoveryOutcome.DraftRestored,
            reason: reason,
            response: status is null
                ? null
                : new
                {
                    environment = status.Environment,
                    storeCode = status.StoreCode,
                    deviceCode = status.DeviceCode,
                    sessionId = status.SessionId,
                    status = status.Status,
                    txnRef = NormalizeOptional(status.TxnRef),
                    responseCode = status.ResponseCode,
                    responseText = status.ResponseText,
                    recoveryAction = status.RecoveryAction,
                    lastHttpStatus = status.LastHttpStatus
                },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                outcome = outcome.ToString(),
                certCase = GetRecoveryCertificationCase(outcome, reason),
                error,
                attemptGuid = attempt?.AttemptGuid,
                transactionReference = NormalizeOptional(attempt?.SessionId) ??
                    NormalizeOptional(status?.SessionId) ??
                    NormalizeOptional(attempt?.TxnRef) ??
                    NormalizeOptional(status?.TxnRef),
                localStatus = attempt?.Status.ToString(),
                attemptSessionId = NormalizeOptional(attempt?.SessionId),
                statusSessionId = NormalizeOptional(status?.SessionId),
                txnRef = NormalizeOptional(attempt?.TxnRef),
                statusTxnRef = NormalizeOptional(status?.TxnRef),
                txnType = attempt?.TxnType,
                amount = attempt?.Amount,
                storeCode = attempt?.StoreCode ?? status?.StoreCode,
                deviceCode = attempt?.DeviceCode ?? status?.DeviceCode,
                cashierId = attempt?.CashierId,
                responseCode = status?.ResponseCode,
                responseText = status?.ResponseText
            },
            // 只有"结果未知"需要人工对账记 Warning；无待恢复/仍在查询/拒付等正常结论保持 Information。
            level: outcome == CardPaymentRecoveryOutcome.Unknown ? LinklyLogLevel.Warning : LinklyLogLevel.Information);
    }

    private static string GetRecoveryCertificationCase(CardPaymentRecoveryOutcome outcome, string reason)
    {
        if (reason.Contains("approved", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("declined", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "3.1.2/4.1.2";
        }

        return "4.1.2";
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool HasPersistedLinklyPaymentEvidence(LocalCardPaymentAttempt attempt)
    {
        return NormalizeOptional(attempt.PaymentReference) is not null ||
            LinklyApprovalResponseCodes.IsApproved(attempt.ResponseCode);
    }

    private static bool TextEquals(string? left, string? right)
    {
        return string.Equals(NormalizeOptional(left), NormalizeOptional(right), StringComparison.OrdinalIgnoreCase);
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

    private static Task RunLocalStoreAsync(Func<Task> operation, CancellationToken cancellationToken) =>
        Task.Run(operation, cancellationToken);

    private static Task<T> RunLocalStoreAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken) =>
        Task.Run(operation, cancellationToken);
}
