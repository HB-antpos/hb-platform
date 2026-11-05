using System.Globalization;
using System.IO;
using System.Text.Json;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>
/// 卡交易恢复里的 Zeller 提供方：给恢复协调器提供队列、逐条恢复、启动/登录时的自动恢复和主管结案。
/// </summary>
/// <remarks>
/// 中文注释：做法照搬 Square 的 CompleteVerifiedAttemptAsync——恢复服务自己按刷卡前的草稿补单：
/// 在隔离的购物车上重建订单，订单 GUID 用草稿里的那个；保存前先查这个订单是否已经存在，存在就必须带着同一个
/// ZELLER_ATTEMPT 付款键，然后只把尝试标记为已建单，不会重复建单。
/// Zeller 尝试表里的 Approved（已批准、订单还没落盘）相当于 Square 的 FinalizePending。
/// 不做的事（和 Square 不同）：不把草稿交回付款页（没有草稿交接）；卡退款只覆盖部分退款金额时不自动补单，留给主管；
/// 草稿里待开的退款代金券不在这里补开。
/// </remarks>
public sealed class ZellerCardRecoveryProvider
{
    internal const int HistoryLimit = 200;

    /// <summary>已批准但订单没落盘的记录，至少空闲这么久才由自动恢复补单，避免和正在进行的收款抢着落单。</summary>
    internal static readonly TimeSpan AutoCompletionIdle = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ZellerPaymentRecoveryService _recovery;
    private readonly ZellerCardTerminalClient _client;
    private readonly ILocalZellerPaymentAttemptRepository _attempts;
    private readonly CashCheckoutService _checkout;
    private readonly ILocalOrderRepository _orderRepository;
    private readonly ICardTerminalSettingsProvider _settingsProvider;
    private readonly ILocalizationService? _localization;
    private readonly FinancialSupervisorAuditReplayService? _supervisorAuditReplay;
    private readonly ISharedHeldOrderPaymentSourceResolver? _heldOrderPaymentSourceResolver;
    private readonly TimeProvider _time;

    public ZellerCardRecoveryProvider(
        ZellerPaymentRecoveryService recovery,
        ZellerCardTerminalClient client,
        ILocalZellerPaymentAttemptRepository attempts,
        CashCheckoutService checkout,
        ILocalOrderRepository orderRepository,
        ICardTerminalSettingsProvider settingsProvider,
        ILocalizationService? localization = null,
        FinancialSupervisorAuditReplayService? supervisorAuditReplay = null,
        ISharedHeldOrderRepository? sharedHeldOrderRepository = null,
        TimeProvider? timeProvider = null)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        _checkout = checkout ?? throw new ArgumentNullException(nameof(checkout));
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _localization = localization;
        _supervisorAuditReplay = supervisorAuditReplay;
        _heldOrderPaymentSourceResolver = sharedHeldOrderRepository is null
            ? null
            : new SharedHeldOrderPaymentSourceResolver(sharedHeldOrderRepository, new SharedHeldOrderReverseMapper());
        _time = timeProvider ?? TimeProvider.System;
    }

    // ───────────── 队列 ─────────────

    /// <summary>未结的记录：已发出没结果、结果未知，以及已批准但订单还没落盘的。</summary>
    public async Task<IReadOnlyList<CardRecoveryQueueItem>> ListOpenAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var environment = await EnvironmentAsync(cancellationToken);
        return (await ListOpenAttemptsAsync(session, cancellationToken))
            .Select(attempt => ToQueueItem(attempt, environment, isOpen: true))
            .ToArray();
    }

    /// <summary>历史页：未结的记录加上最近的已结记录。</summary>
    public async Task<IReadOnlyList<CardRecoveryQueueItem>> ListHistoryAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var environment = await EnvironmentAsync(cancellationToken);
        var open = await ListOpenAttemptsAsync(session, cancellationToken);
        var openGuids = open.Select(attempt => attempt.AttemptGuid).ToHashSet();
        var recent = await _attempts.ListRecentAsync(session.StoreCode, session.DeviceCode, HistoryLimit, cancellationToken);
        return open
            .Select(attempt => ToQueueItem(attempt, environment, isOpen: true))
            .Concat(recent
                .Where(attempt => !IsInstallmentAttempt(attempt))
                .Where(attempt => !openGuids.Contains(attempt.AttemptGuid))
                .Select(attempt => ToQueueItem(attempt, environment, isOpen: false)))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.CreatedAt)
            .ToArray();
    }

    // ───────────── 恢复 ─────────────

    /// <summary>付款页「检查上一笔」：取这台收银机最新的一条未结记录恢复。</summary>
    public async Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var latest = (await ListOpenAttemptsAsync(session, cancellationToken)).FirstOrDefault();
        return latest is null
            ? CardPaymentRecoveryResult.None
            : await RecoverAttemptAsync(latest.AttemptGuid, cart, session, cancellationToken);
    }

    /// <summary>
    /// 恢复一条记录：已批准就按草稿补单；已发出或结果未知就按 reference 向 Zeller 查一次，查到批准再补单。
    /// </summary>
    public async Task<CardPaymentRecoveryResult> RecoverAttemptAsync(
        Guid attemptGuid,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var attempt = await _attempts.GetAsync(attemptGuid, cancellationToken);
        if (attempt is null || !SameTerminal(attempt, session) || IsInstallmentAttempt(attempt))
        {
            return CardPaymentRecoveryResult.None;
        }

        switch (attempt.Status)
        {
            case ZellerAttemptStatus.Approved:
                return await CompleteOrderAsync(attempt);

            case ZellerAttemptStatus.Submitted:
            case ZellerAttemptStatus.Unknown:
                var recovered = await _recovery.RecoverAttemptAsync(attemptGuid, cancellationToken);
                return recovered.Status switch
                {
                    ZellerRecoveryStatus.InProgress => new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Checking,
                        T("cardRecovery.zeller.inProgress", "This Zeller payment is still being checked on this POS. Try again in a moment.")),
                    ZellerRecoveryStatus.StillUnknown => new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        UnknownMessage()),
                    ZellerRecoveryStatus.Resolved when recovered.Attempt?.Status == ZellerAttemptStatus.Approved =>
                        await CompleteOrderAsync(recovered.Attempt),
                    ZellerRecoveryStatus.Resolved => new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.None,
                        T("cardRecovery.zeller.notPaid", "Zeller reports this payment was not approved. The customer was not charged.")),
                    _ => CardPaymentRecoveryResult.None
                };

            default:
                return CardPaymentRecoveryResult.None;
        }
    }

    /// <summary>
    /// 程序启动、收银员登录时：补做迟到复核、关闭从未发出的记录、按 reference 查未结记录，再把已批准但订单没落盘的补单。
    /// </summary>
    public async Task<CardAutoRecoverySummary> AutoRecoverDeterministicAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var summary = await _recovery.AutoRecoverAsync(session.StoreCode, session.DeviceCode, cancellationToken);
        var idleBefore = _time.GetUtcNow() - AutoCompletionIdle;
        var recoveredOrders = 0;
        foreach (var attempt in summary.AwaitingOrderCompletion.Where(attempt => attempt.UpdatedAt <= idleBefore && !IsInstallmentAttempt(attempt)))
        {
            try
            {
                var result = await CompleteOrderAsync(attempt);
                if (result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted)
                {
                    recoveredOrders++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                ConsoleLog.WriteWarning(
                    "ZellerRecovery",
                    $"auto recovery could not complete the order attemptGuid={attempt.AttemptGuid:D} error={ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                    ex);
            }
        }

        return new CardAutoRecoverySummary(summary.Results.Count + summary.AwaitingOrderCompletion.Count, recoveredOrders);
    }

    // ───────────── 主管结案 ─────────────

    /// <summary>
    /// 恢复中心的主管结案。以人工确认为准；决定和本地结案日志在同一个事务里写入，提交后上传审计。
    /// 确认已付款时随即按草稿补单。确认未付款不把草稿交回付款页：付款页再「检查上一笔」时这条已结，自然解锁。
    /// </summary>
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
        var attempt = await _attempts.GetAsync(attemptGuid, cancellationToken);
        if (attempt is null || !SameTerminal(attempt, session) || IsInstallmentAttempt(attempt))
        {
            return new CardRecoveryResolutionResult(
                false,
                T("cardRecovery.zeller.notFound", "This Zeller payment was not found on this POS."),
                LockRetained: true);
        }

        if (attempt.Status == ZellerAttemptStatus.Approved)
        {
            // Zeller 已经批准（或之前已确认已付款）：不需要再决定，只差补单。
            var completed = await CompleteOrderAsync(attempt);
            return new CardRecoveryResolutionResult(
                completed.Outcome == CardPaymentRecoveryOutcome.OrderCompleted,
                completed.Message,
                completed,
                LockRetained: completed.Outcome != CardPaymentRecoveryOutcome.OrderCompleted);
        }

        var authorizer = OperationAuthorizationScope.CurrentAuthorizingSession ?? session.CashierSession;
        var operatorId = authorizer?.CashierId ?? session.CashierId;
        var operatorName = authorizer?.CashierName ?? session.CashierName;
        var operatorUserGuid = authorizer?.UserGuid ?? session.CashierSession?.UserGuid;
        var zellerDecision = decision switch
        {
            CardRecoverySupervisorDecision.ConfirmProcessed => ZellerSupervisorDecision.ConfirmPaid,
            CardRecoverySupervisorDecision.ConfirmNotProcessed => ZellerSupervisorDecision.ConfirmNotPaid,
            _ => ZellerSupervisorDecision.ContinueWaiting
        };
        var environment = await EnvironmentAsync(cancellationToken);
        var resolution = new ZellerSupervisorResolution(zellerDecision, operatorId, operatorName, reason, reference);
        var result = await _recovery.ResolveAsync(
            attemptGuid,
            resolution,
            resolved => BuildSupervisorJournal(resolved, decision, reason, evidence, reference, environment, operatorId, operatorUserGuid, operatorName),
            cancellationToken);
        if (!result.Succeeded)
        {
            return new CardRecoveryResolutionResult(false, result.Error ?? UnknownMessage(), LockRetained: true);
        }

        await PersistAuditAfterCommitAsync(result.Journal, attemptGuid);
        switch (zellerDecision)
        {
            case ZellerSupervisorDecision.ContinueWaiting:
                return new CardRecoveryResolutionResult(
                    true,
                    T("cardRecovery.zeller.keepWaiting", "The Zeller payment stays open. Check it again later."),
                    LockRetained: true,
                    ResolutionPersisted: true,
                    ResolutionApplied: true);

            case ZellerSupervisorDecision.ConfirmNotPaid:
                return new CardRecoveryResolutionResult(
                    true,
                    T("cardRecovery.zeller.confirmedNotPaid", "Recorded as not paid. The customer can pay again."),
                    RetryAllowed: true,
                    ResolutionPersisted: true,
                    ResolutionApplied: true);

            default:
                var completed = await CompleteOrderAsync(result.Attempt!);
                return new CardRecoveryResolutionResult(
                    completed.Outcome == CardPaymentRecoveryOutcome.OrderCompleted,
                    completed.Message,
                    completed,
                    LockRetained: completed.Outcome != CardPaymentRecoveryOutcome.OrderCompleted,
                    ResolutionPersisted: true,
                    ResolutionApplied: true);
        }
    }

    // ───────────── 补单 ─────────────

    /// <summary>
    /// 按刷卡前的草稿把已批准的 Zeller 付款补成订单。已经有同一个订单 GUID 的订单时只做核对和标记，不再保存。
    /// 任何一步不能确认都返回 Unknown，不会冒险建单。
    /// </summary>
    internal async Task<CardPaymentRecoveryResult> CompleteOrderAsync(LocalZellerPaymentAttempt attempt)
    {
        if (IsInstallmentAttempt(attempt))
        {
            // 中文注释：分期续付的记录里存的是续付请求，不是销售草稿；按草稿补单会多出一张销售单。由分期操作恢复。
            Log($"installment attempt is not completed from a sale draft attemptGuid={attempt.AttemptGuid:D}");
            return Unknown();
        }

        if (!CardRecoveryCartMaterializer.TryPrepare(attempt.OrderDraftJson, JsonOptions, out var draft) || draft is null)
        {
            Log($"draft could not be read attemptGuid={attempt.AttemptGuid:D}");
            return Unknown();
        }

        string? invalid = ValidateDraft(attempt, draft);
        if (invalid is not null)
        {
            Log($"draft rejected attemptGuid={attempt.AttemptGuid:D} reason={invalid}");
            return Unknown();
        }

        var tenderKey = CashPaymentWorkflowService.FormatZellerAttemptTenderKey(attempt.AttemptGuid);
        LocalOrder? existingOrder;
        try
        {
            existingOrder = await _orderRepository.GetOrderAsync(draft.OrderGuid, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log($"existing order lookup failed attemptGuid={attempt.AttemptGuid:D} error={ex.GetType().Name}", ex);
            return Unknown();
        }

        if (existingOrder is not null)
        {
            if (!existingOrder.Payments.Any(payment =>
                    payment.Method == PaymentMethodKind.Card &&
                    string.Equals(payment.IdempotencyKey, tenderKey, StringComparison.Ordinal)))
            {
                // 同一个订单 GUID 已经被别的付款占用：不能确认是这一笔，交给主管。
                Log($"existing order does not carry the attempt key attemptGuid={attempt.AttemptGuid:D} order={draft.OrderGuid:D}");
                return Unknown();
            }

            var markedExisting = await _client.MarkOrderCompletedAsync(attempt.AttemptGuid, CancellationToken.None);
            return Completed(existingOrder, hasPostCommitWarning: !markedExisting && attempt.Status == ZellerAttemptStatus.Approved);
        }

        var isRefund = attempt.OperationKind == ZellerAttemptOperation.Refund;
        var cardAmount = Math.Abs(draft.CardAmount);
        var paymentReference = ZellerPaymentRecoveryService.OrderReferenceFor(attempt);
        var tenderReference = isRefund ? CardRefundReference.Format(paymentReference, draft.OriginalReference!) : paymentReference;
        var cardTender = new PaymentTender(
            PaymentMethodKind.Card,
            isRefund ? -cardAmount : cardAmount,
            tenderReference,
            CardTransactions: [BuildCardTransaction(attempt, cardAmount)],
            IdempotencyKey: tenderKey);
        var tenders = draft.CurrentTenders.Concat([cardTender]).ToList();
        if (isRefund && Math.Abs(RoundCurrency(tenders.Sum(tender => tender.Amount))) < Math.Abs(RoundCurrency(draft.ActualAmount)))
        {
            // 卡退款只覆盖了部分退货金额，余下的还没付：不能自动补单，交给主管在付款页处理。
            Log($"partial refund cannot be completed automatically attemptGuid={attempt.AttemptGuid:D}");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.zeller.partialRefund", "The Zeller refund was approved but only covers part of the return, so the order cannot be completed automatically. Ask a supervisor to finish the return."));
        }

        var cashTenderedAmount = tenders.Where(tender => tender.Method == PaymentMethodKind.Cash).Sum(tender => tender.Amount);
        LocalOrder order;
        try
        {
            var recoveryCart = new PosCartService();
            recoveryCart.RestoreSnapshot(draft.CartSnapshot);
            order = _checkout.CreatePaymentOrder(recoveryCart, draft.Session, tenders, cashTenderedAmount).Order with { OrderGuid = draft.OrderGuid };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log($"order rebuild failed attemptGuid={attempt.AttemptGuid:D} error={ex.GetType().Name}", ex);
            return Unknown();
        }

        try
        {
            var heldOrder = await TryResolveHeldOrderAsync(draft.Session, draft.CartSnapshot);
            if (heldOrder is null)
            {
                await _orderRepository.SavePendingOrderAsync(order, CancellationToken.None);
            }
            else
            {
                await _orderRepository.SavePendingOrderWithHeldSourceAsync(order, heldOrder, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "ZellerRecovery",
                $"recovered order save failed attemptGuid={attempt.AttemptGuid:D} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return Unknown();
        }

        var hasPostCommitWarning = false;
        try
        {
            hasPostCommitWarning = !await _client.MarkOrderCompletedAsync(attempt.AttemptGuid, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            Log($"order saved but the attempt could not be marked completed attemptGuid={attempt.AttemptGuid:D} error={ex.GetType().Name}", ex);
        }

        ConsoleLog.Write("ZellerRecovery", $"order recovered from draft attemptGuid={attempt.AttemptGuid:D} order={order.OrderGuid:D}");
        return Completed(order, hasPostCommitWarning);
    }

    // ───────────── 辅助 ─────────────

    private async Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListOpenAttemptsAsync(PosSessionState session, CancellationToken cancellationToken)
    {
        var open = await _recovery.ListOpenAsync(session.StoreCode, session.DeviceCode, cancellationToken);
        var awaiting = await _recovery.ListAwaitingOrderCompletionAsync(session.StoreCode, session.DeviceCode, cancellationToken);
        return open
            .Concat(awaiting)
            .Where(attempt => !IsInstallmentAttempt(attempt))
            .OrderByDescending(attempt => attempt.UpdatedAt)
            .ThenByDescending(attempt => attempt.CreatedAt)
            .ToArray();
    }

    private async Task<string> EnvironmentAsync(CancellationToken cancellationToken) =>
        (await _settingsProvider.GetSettingsAsync(cancellationToken)).Environment.ToString();

    internal static CardRecoveryQueueItem ToQueueItem(LocalZellerPaymentAttempt attempt, string environment, bool isOpen) =>
        new(
            CardProcessorKind.Zeller,
            attempt.AttemptGuid,
            attempt.OperationKind == ZellerAttemptOperation.Refund ? "Refund" : "Sale",
            attempt.AmountCents / 100m,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.CashierId,
            environment,
            attempt.Status.ToString(),
            attempt.CreatedAt,
            attempt.UpdatedAt,
            attempt.OrderDraftJson,
            null,
            attempt.Reference,
            null,
            attempt.ResponseCode,
            // 中文注释：恢复中心这一栏只做显示。没有交易响应文字时显示 SDK 错误，主管能看出是刷卡机拒绝、超时还是取消。
            attempt.ResponseText ?? FormatCallError(attempt),
            null,
            attempt.TransactionUuid,
            attempt.OperationGuid,
            attempt.Status == ZellerAttemptStatus.Approved ? "APPROVED" : null)
        {
            IsOpen = isOpen,
            ReceiptApprovalReference = attempt.ApprovalCode ?? attempt.Rrn
        };

    internal static string? FormatCallError(LocalZellerPaymentAttempt attempt) =>
        attempt.CallErrorType is null
            ? null
            : attempt.CallErrorMessage is null
                ? attempt.CallErrorType
                : $"{attempt.CallErrorType}: {attempt.CallErrorMessage}";

    /// <summary>草稿和尝试必须是同一件事：订单 GUID、门店/设备/收银员、销售/退款、金额（分）、付款符号。不通过返回原因。</summary>
    internal static string? ValidateDraft(LocalZellerPaymentAttempt attempt, CardPaymentOrderDraft draft)
    {
        var isRefund = attempt.OperationKind == ZellerAttemptOperation.Refund;
        if (draft.OrderGuid == Guid.Empty || draft.Session is null || draft.CartSnapshot is null || draft.CurrentTenders is null)
        {
            return "incomplete";
        }

        if (!SameText(draft.Session.StoreCode, attempt.StoreCode) ||
            !SameText(draft.Session.DeviceCode, attempt.DeviceCode) ||
            !SameText(draft.Session.CashierId, attempt.CashierId) ||
            !SameText(draft.TxnType, isRefund ? "R" : "P"))
        {
            return "identity";
        }

        var cardCents = Math.Abs(draft.CardAmount) * 100m;
        if (cardCents != decimal.Truncate(cardCents) || cardCents != attempt.AmountCents)
        {
            return "amount";
        }

        var cart = new PosCartService();
        cart.RestoreSnapshot(draft.CartSnapshot);
        if (RoundCurrency(cart.ActualAmount) != RoundCurrency(draft.ActualAmount))
        {
            return "cart";
        }

        if (isRefund)
        {
            return draft.ActualAmount < 0m &&
                !string.IsNullOrWhiteSpace(draft.OriginalReference) &&
                draft.CurrentTenders.All(tender => tender.Amount < 0m)
                ? null
                : "refund-semantics";
        }

        var tenderTotal = RoundCurrency(draft.CurrentTenders.Sum(tender => tender.Amount));
        return draft.ActualAmount > 0m &&
            draft.CurrentTenders.All(tender => tender.Amount > 0m) &&
            RoundCurrency(tenderTotal + Math.Abs(draft.CardAmount)) >= RoundCurrency(draft.ActualAmount)
            ? null
            : "sale-semantics";
    }

    /// <summary>分期续付的 Zeller 记录：不进卡交易恢复中心、不按销售草稿补单，由分期操作按同一条记录恢复。</summary>
    internal static bool IsInstallmentAttempt(LocalZellerPaymentAttempt attempt) =>
        attempt.OperationKind == ZellerAttemptOperation.InstallmentRepayment;

    internal static CardTransactionDto BuildCardTransaction(LocalZellerPaymentAttempt attempt, decimal amount)
    {
        // 主管按已付款结案、没有交易号时，和 Square 一样标成「Zeller Supervisor」，便于对账时区分。
        var supervisorConfirmed = string.IsNullOrWhiteSpace(attempt.TransactionUuid) &&
            attempt.SupervisorDecision == ZellerSupervisorDecision.ConfirmPaid;
        return new CardTransactionDto(
            supervisorConfirmed ? "Zeller Supervisor" : ZellerPaymentReference.Processor,
            TxnRef: attempt.TransactionUuid ?? attempt.Reference,
            AuthCode: string.IsNullOrWhiteSpace(attempt.ApprovalCode) ? null : attempt.ApprovalCode,
            CardType: attempt.Scheme,
            CardBin: int.TryParse(attempt.Bin, NumberStyles.None, CultureInfo.InvariantCulture, out var bin) ? bin : null,
            MaskedCardNumber: attempt.PanMasked,
            MerchantId: attempt.MerchantId,
            ResponseCode: attempt.ResponseCode,
            ResponseText: supervisorConfirmed ? "Supervisor confirmed paid." : attempt.ResponseText,
            Stan: null,
            BankDateTime: attempt.TransactionTimestamp,
            Amount: amount,
            ReceiptText: string.IsNullOrWhiteSpace(attempt.ReceiptText) ? null : attempt.ReceiptText);
    }

    private static LocalFinancialSupervisorResolution BuildSupervisorJournal(
        LocalZellerPaymentAttempt attempt,
        CardRecoverySupervisorDecision decision,
        string reason,
        string? evidence,
        string? reference,
        string environment,
        string operatorCashierId,
        string? operatorUserGuid,
        string operatorName)
    {
        var isRefund = attempt.OperationKind == ZellerAttemptOperation.Refund;
        var resolvedAt = attempt.SupervisorDecidedAt ?? attempt.UpdatedAt;
        var auditEventId = Guid.NewGuid();
        var auditEvent = new OperationAuditEventDto
        {
            EventId = auditEventId,
            OccurredAtUtc = resolvedAt,
            OperationType = isRefund ? "CARD_REFUND_SUPERVISOR_RESOLUTION" : "CARD_PAYMENT_SUPERVISOR_RESOLUTION",
            Outcome = "Succeeded",
            CashierId = operatorCashierId,
            UserGuid = operatorUserGuid,
            CashierName = operatorName,
            StoreCode = attempt.StoreCode,
            DeviceCode = attempt.DeviceCode,
            CorrelationId = attempt.AttemptGuid.ToString("D"),
            PaymentMethod = CardProcessorKind.Zeller.ToString(),
            ReasonCode = decision.ToString(),
            SafeMessage = reason,
            PaymentAmount = attempt.AmountCents / 100m,
            Properties = new Dictionary<string, string?>
            {
                ["attemptGuid"] = attempt.AttemptGuid.ToString("D"),
                ["operationGuid"] = attempt.OperationGuid?.ToString("D"),
                ["reference"] = attempt.Reference,
                ["transactionUuid"] = attempt.TransactionUuid,
                ["evidence"] = evidence,
                ["financialReference"] = reference
            }
        };

        // 销售照 Square 记成 ActiveSession（会话号用 Zeller reference），退款记成 CardRefund（重试引用用退款 reference）。
        return new LocalFinancialSupervisorResolution(
            Guid.NewGuid(),
            isRefund ? LocalFinancialSupervisorResolutionTarget.CardRefund : LocalFinancialSupervisorResolutionTarget.ActiveSession,
            CardProcessorKind.Zeller.ToString(),
            environment,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.AttemptGuid,
            null,
            attempt.OperationGuid,
            isRefund ? null : attempt.Reference,
            decision.ToString(),
            operatorCashierId,
            operatorUserGuid,
            operatorName,
            reason,
            evidence,
            reference,
            isRefund ? attempt.Reference : null,
            resolvedAt,
            auditEventId,
            JsonSerializer.Serialize(auditEvent, JsonOptions));
    }

    private async Task PersistAuditAfterCommitAsync(LocalFinancialSupervisorResolution? journal, Guid attemptGuid)
    {
        if (journal is null || _supervisorAuditReplay is null)
        {
            return;
        }

        try
        {
            await _supervisorAuditReplay.PersistAfterCommitAsync(journal, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 日志已经和决定一起落库，审计上传失败由后台重放补上。
            Log($"supervisor audit upload deferred attemptGuid={attemptGuid:D} error={ex.GetType().Name}", ex);
        }
    }

    private async Task<LocalHeldOrderCompletionContext?> TryResolveHeldOrderAsync(PosSessionState session, PosCartSnapshot cartSnapshot)
    {
        if (_heldOrderPaymentSourceResolver is null)
        {
            // 来源解析器缺失时，已显式绑定共享挂单的购物车绝不能静默降级为普通订单（和 Square 一致）。
            if (cartSnapshot.SharedHeldOrderClaimId is not null)
            {
                throw new InvalidDataException("共享挂单购物车 binding 但来源解析器不可用，拒绝降级为普通订单。");
            }

            return null;
        }

        return await _heldOrderPaymentSourceResolver.TryResolveAsync(session, cartSnapshot, CancellationToken.None);
    }

    private CardPaymentRecoveryResult Completed(LocalOrder order, bool hasPostCommitWarning) =>
        new(
            CardPaymentRecoveryOutcome.OrderCompleted,
            T("cardRecovery.zeller.approved", "The previous Zeller card payment was successful. The order has been recovered and saved automatically."),
            order,
            HasPostCommitWarning: hasPostCommitWarning);

    private CardPaymentRecoveryResult Unknown() => new(CardPaymentRecoveryOutcome.Unknown, UnknownMessage());

    private string UnknownMessage() =>
        T("cardRecovery.zeller.unknown", "The Zeller payment result could not be confirmed. Check it with a supervisor before taking payment again.");

    private static bool SameTerminal(LocalZellerPaymentAttempt attempt, PosSessionState session) =>
        SameText(attempt.StoreCode, session.StoreCode) && SameText(attempt.DeviceCode, session.DeviceCode);

    private static bool SameText(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static decimal RoundCurrency(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private string T(string key, string fallback)
    {
        var text = _localization?.T(key);
        return string.IsNullOrWhiteSpace(text) || text.StartsWith("[[", StringComparison.Ordinal) ? fallback : text;
    }

    private static void Log(string message, Exception? exception = null) =>
        ConsoleLog.WriteWarning("ZellerRecovery", message, exception: exception);
}
