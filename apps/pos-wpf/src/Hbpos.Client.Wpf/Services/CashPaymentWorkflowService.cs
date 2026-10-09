using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hbpos.Contracts.Linkly;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ICashPaymentWorkflowService
{
    bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount);

    decimal CalculateChange(string? amountTenderedText, decimal actualAmount);

    decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders);

    decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders);

    decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount);

    Task<PaymentTenderAttemptResult> AddTenderAsync(
        PaymentMethodKind method,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        string? amountText,
        string? referenceText = null,
        CancellationToken cancellationToken = default,
        PosCartSnapshot? cartSnapshot = null);

    Task<bool> ReleaseVoucherTenderAsync(
        PaymentTender tender,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    Task<PaymentTenderAttemptResult> AddManualCardTenderAsync(
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        string? amountText,
        Guid confirmationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(PaymentTenderAttemptResult.Fail("payment.status.unsupportedMethod"));

    Task<CashPaymentWorkflowResult> CompleteAsync(
        PosCartService cart,
        PosSessionState session,
        string? amountTenderedText,
        CancellationToken cancellationToken = default);

    Task<CashPaymentWorkflowResult> CompletePaymentAsync(
        PosCartService cart,
        PosSessionState session,
        IReadOnlyList<PaymentTender> tenders,
        decimal cashTenderedAmount,
        CancellationToken cancellationToken = default);

    Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
        Guid orderGuid,
        PosCartService cart,
        PosSessionState session,
        decimal tenderedAmount,
        decimal changeAmount,
        CancellationToken cancellationToken = default);
}

public sealed class CashPaymentWorkflowService(
    CashCheckoutService checkout,
    ILocalOrderRepository orderRepository,
    ISyncQueueRepository syncQueueRepository,
    IOrderUploadService? orderUploadService = null,
    ICardTerminalClient? cardTerminalClient = null,
    IVoucherTenderClient? voucherTenderClient = null,
    ILocalCardPaymentAttemptRepository? cardPaymentAttemptRepository = null,
    ICardTerminalSettingsProvider? cardTerminalSettingsProvider = null,
    ILocalSquarePaymentAttemptRepository? squarePaymentAttemptRepository = null,
    ILinklyPaymentAttemptContextAccessor? linklyPaymentAttemptContextAccessor = null,
    ISquarePaymentAttemptContextAccessor? squarePaymentAttemptContextAccessor = null,
    ILinklyBackendTerminalClient? linklyBackendTerminalClient = null,
    IEnumerable<ICardPaymentResultPolicy>? cardPaymentResultPolicies = null,
    ISharedHeldOrderRepository? sharedHeldOrderRepository = null) : ICashPaymentWorkflowService
{
    private static readonly JsonSerializerOptions CardAttemptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CashRoundingPolicy _cashRoundingPolicy = new();
    private readonly ICardTerminalClient _cardTerminalClient = cardTerminalClient ?? UnavailableCardTerminalClient.Instance;
    private readonly IVoucherTenderClient _voucherTenderClient = voucherTenderClient ?? UnavailableVoucherTenderClient.Instance;
    private readonly CardPaymentResultPolicyResolver _cardPaymentResultPolicyResolver = new(
        cardPaymentResultPolicies ??
        [
            new LinklyCardPaymentResultPolicy(),
            new SquareCardPaymentResultPolicy(),
            new FallbackCardPaymentResultPolicy()
        ]);
    private readonly ISharedHeldOrderPaymentSourceResolver? _heldOrderPaymentSourceResolver =
        sharedHeldOrderRepository is null
            ? null
            : new SharedHeldOrderPaymentSourceResolver(
                sharedHeldOrderRepository,
                new SharedHeldOrderReverseMapper());

    private readonly record struct RecoverableOrderPersistence(
        LocalOrder Order,
        bool AlreadyPersisted);

    public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount)
    {
        if (string.IsNullOrWhiteSpace(amountTenderedText))
        {
            tenderedAmount = 0m;
            return false;
        }

        return decimal.TryParse(amountTenderedText, NumberStyles.Number, CultureInfo.CurrentCulture, out tenderedAmount)
            || decimal.TryParse(amountTenderedText, NumberStyles.Number, CultureInfo.InvariantCulture, out tenderedAmount);
    }

    public decimal CalculateChange(string? amountTenderedText, decimal actualAmount)
    {
        if (RoundCurrency(actualAmount) < 0m)
        {
            return 0m;
        }

        if (!TryParseTenderedAmount(amountTenderedText, out var tenderedAmount))
        {
            return 0m;
        }

        var normalizedTenderedAmount = _cashRoundingPolicy.NormalizeCashTender(tenderedAmount);
        var roundedCashDue = _cashRoundingPolicy.CalculateRoundedCashDue(actualAmount);
        return _cashRoundingPolicy.CalculateChange(normalizedTenderedAmount, roundedCashDue);
    }

    public decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders)
    {
        return RoundCurrency(tenders.Sum(tender => NormalizeTender(tender).Amount));
    }

    public decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders)
    {
        actualAmount = RoundCurrency(actualAmount);
        if (actualAmount < 0m)
        {
            return CalculateRefundRemainingAmount(actualAmount, tenders);
        }

        var normalizedTenders = tenders.Select(NormalizeTender).ToList();
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var cashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        if (cashTotal <= 0m)
        {
            return RoundCurrency(actualAmount - nonCashTotal);
        }

        var roundedCashDue = _cashRoundingPolicy.CalculateRoundedCashDue(actualAmount, nonCashTotal);
        return RoundCurrency(roundedCashDue - cashTotal);
    }

    public decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount)
    {
        if (RoundCurrency(actualAmount) < 0m)
        {
            return 0m;
        }

        var normalizedTenders = tenders.Select(NormalizeTender).ToList();
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var cashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        if (cashTotal <= 0m)
        {
            return 0m;
        }

        var roundedCashDue = _cashRoundingPolicy.CalculateRoundedCashDue(actualAmount, nonCashTotal);
        return _cashRoundingPolicy.CalculateChange(cashTotal, roundedCashDue);
    }

    public Task<PaymentTenderAttemptResult> AddManualCardTenderAsync(
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        string? amountText,
        Guid confirmationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RoundCurrency(actualAmount) <= 0m || confirmationId == Guid.Empty ||
            currentTenders.Any(tender => tender.Method == PaymentMethodKind.Card))
        {
            return Task.FromResult(PaymentTenderAttemptResult.Fail("payment.status.unsupportedMethod"));
        }

        if (!TryParseTenderedAmount(amountText, out var amount) || amount <= 0m || amount != RoundCurrency(amount))
        {
            return Task.FromResult(PaymentTenderAttemptResult.Fail("payment.status.invalidAmount"));
        }

        var remaining = CalculateExternalRemainingAmount(actualAmount, currentTenders);
        if (remaining <= 0m || amount != remaining)
        {
            return Task.FromResult(PaymentTenderAttemptResult.Fail(
                amount > remaining ? "payment.status.cardExceedsRemaining" : "payment.status.cardMustBeFinalTender"));
        }

        // 中文注释：调用方只能在人工核实成功后进入此方法；不读取终端设置、不发起任何扣款。
        var reference = ManualCardPaymentReference.Format(confirmationId);
        var transaction = new CardTransactionDto(
            ManualCardPaymentReference.Processor, reference, null, null, null, null, null,
            null, "Manually confirmed by cashier", null, null, amount, null);
        return Task.FromResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, amount, reference,
                CardTransactions: [transaction], IdempotencyKey: reference),
            "payment.status.cardTenderAdded"));
    }

    public async Task<PaymentTenderAttemptResult> AddTenderAsync(
        PaymentMethodKind method,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        string? amountText,
        string? referenceText = null,
        CancellationToken cancellationToken = default,
        PosCartSnapshot? cartSnapshot = null)
    {
        if (!TryParseTenderedAmount(amountText, out var amount) || amount <= 0m)
        {
            return PaymentTenderAttemptResult.Fail("payment.status.invalidAmount");
        }

        var isRefund = RoundCurrency(actualAmount) < 0m;
        var remainingAmount = CalculateRemainingAmount(actualAmount, currentTenders);
        if ((!isRefund && remainingAmount <= 0m) ||
            (isRefund && remainingAmount >= 0m))
        {
            return PaymentTenderAttemptResult.Fail("payment.status.alreadyFullyPaid");
        }

        if (!isRefund &&
            method == PaymentMethodKind.Voucher &&
            HasExistingVoucherTender(currentTenders, referenceText))
        {
            return PaymentTenderAttemptResult.Fail("payment.status.duplicateVoucher");
        }

        if (isRefund)
        {
            if (method == PaymentMethodKind.Card && ManualCardPaymentReference.IsManualRefundSource(referenceText))
            {
                return PaymentTenderAttemptResult.Fail("payment.status.cardDeclined",
                    "This payment was confirmed manually. Refund it using an alternative method; do not send it to a linked terminal.");
            }

            if (method == PaymentMethodKind.Card && string.IsNullOrWhiteSpace(referenceText))
            {
                ConsoleLog.Write("CardRefund", "workflow blocked card refund reason=missing-original-reference");
                return PaymentTenderAttemptResult.Fail("payment.status.cardDeclined", "Original card payment reference is required.");
            }

            return method switch
            {
                PaymentMethodKind.Cash => CreateRefundCashTenderAttempt(amount),
                PaymentMethodKind.Card => await AuthorizeCardTenderAsync(
                    amount,
                    CalculateExternalRemainingAmount(actualAmount, currentTenders),
                    session,
                    actualAmount,
                    currentTenders,
                    cartSnapshot,
                    referenceText,
                    cancellationToken,
                    isRefund: true,
                    "payment.status.cardExceedsRemaining",
                    "payment.status.cardDeclined",
                    "payment.status.cardTenderAdded"),
                PaymentMethodKind.Voucher => AuthorizeRefundTenderAsync(
                    amount,
                    CalculateExternalRemainingAmount(actualAmount, currentTenders),
                    session,
                    referenceText,
                    cancellationToken,
                    PaymentMethodKind.Voucher,
                    "payment.status.voucherExceedsRemaining",
                    "payment.status.voucherTenderAdded"),
                _ => PaymentTenderAttemptResult.Fail("payment.status.unsupportedMethod")
            };
        }

        return method switch
        {
            PaymentMethodKind.Cash => CreateCashTenderAttempt(amount),
            PaymentMethodKind.Card => await AuthorizeCardTenderAsync(
                amount,
                CalculateExternalRemainingAmount(actualAmount, currentTenders),
                session,
                actualAmount,
                currentTenders,
                cartSnapshot,
                null,
                cancellationToken,
                isRefund: false,
                "payment.status.cardExceedsRemaining",
                "payment.status.cardDeclined",
                "payment.status.cardTenderAdded"),
            PaymentMethodKind.Voucher => await AuthorizeExternalTenderAsync(
                amount,
                CalculateExternalRemainingAmount(actualAmount, currentTenders),
                session,
                referenceText,
                cancellationToken,
                _voucherTenderClient.RedeemAsync,
                PaymentMethodKind.Voucher,
                "payment.status.voucherExceedsRemaining",
                "payment.status.voucherDeclined",
                "payment.status.voucherTenderAdded"),
            _ => PaymentTenderAttemptResult.Fail("payment.status.unsupportedMethod")
        };
    }

    public async Task<bool> ReleaseVoucherTenderAsync(
        PaymentTender tender,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        if (tender.Method != PaymentMethodKind.Voucher)
        {
            return false;
        }

        var (voucherCode, reservationToken) = ParseVoucherReservationFromReference(tender.Reference);
        if (string.IsNullOrWhiteSpace(voucherCode) || string.IsNullOrWhiteSpace(reservationToken))
        {
            return false;
        }

        try
        {
            // 中文注释：释放锁定只使用前三段，余额打印扩展段不会参与后端释放。
            return await _voucherTenderClient.ReleaseAsync(session, voucherCode, reservationToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 释放失败会让券额被锁到预占过期：记 Warning；券号只留后 4 位，reservationToken 不进日志。
            ConsoleLog.WriteWarning(
                "VoucherRelease",
                $"release failed voucherTail={VoucherCodeLogFormat.Tail(voucherCode)} amount={tender.Amount:0.00} " +
                $"error={ex.GetType().Name} message={LogValue(ex.Message)}",
                new ApplicationLogContext(
                    StatusCode: ex is CatalogApiException { StatusCode: { } status } ? (int)status : null,
                    Properties: new Dictionary<string, object?>
                    {
                        ["storeCode"] = session.StoreCode,
                        ["deviceCode"] = session.DeviceCode,
                        ["errorCode"] = (ex as CatalogApiException)?.ErrorCode
                    }),
                ex);
            return false;
        }
    }

    public async Task<CashPaymentWorkflowResult> CompleteAsync(
        PosCartService cart,
        PosSessionState session,
        string? amountTenderedText,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseTenderedAmount(amountTenderedText, out var tenderedAmount))
        {
            throw new InvalidOperationException("Tendered amount is invalid.");
        }

        var result = checkout.CreateCashOrder(cart, session, tenderedAmount);
        // 共享挂单取单完成：现金路径必须用正在结账的购物车快照做来源匹配，
        // 避免把无关 open claim 误绑到普通订单。
        var cartSnapshot = cart.CreateSnapshot();
        // 共享挂单取单完成：现金路径与 LocalOrder 同一事务写入来源/完成 claim/消费本地挂单。
        var heldOrder = await TryResolveHeldOrderAsync(session, cartSnapshot, cancellationToken);
        await RunLocalStoreAsync(
            () => heldOrder is null
                ? orderRepository.SavePendingOrderAsync(result.Order, cancellationToken)
                : orderRepository.SavePendingOrderWithHeldSourceAsync(
                    result.Order,
                    heldOrder,
                    cancellationToken),
            cancellationToken);

        var hasPostCommitWarning = TryClearCartAfterCommit(cart, result.Order.OrderGuid);
        var pendingSyncResult = await ReadPendingSyncCountAfterCommitAsync(session, cancellationToken);
        hasPostCommitWarning |= pendingSyncResult.HasPostCommitWarning;
        var pendingSyncCount = pendingSyncResult.PendingSyncCount;
        var updatedSession = session with { PendingSyncCount = pendingSyncCount };

        return new CashPaymentWorkflowResult(
            result.Order,
            result.TenderedAmount,
            result.ChangeAmount,
            pendingSyncCount,
            updatedSession,
            hasPostCommitWarning);
    }

    public async Task<CashPaymentWorkflowResult> CompletePaymentAsync(
        PosCartService cart,
        PosSessionState session,
        IReadOnlyList<PaymentTender> tenders,
        decimal cashTenderedAmount,
        CancellationToken cancellationToken = default)
    {
        // owner 必须在任何 await 前固定；后续只允许对这一个恢复 publication 做收尾或回滚。
        var recoveryOwnerAttemptKey = cart.RecoveryOwnerAttemptKey;
        var recoveryOwnerAttemptGuid = recoveryOwnerAttemptKey?.AttemptGuid ??
            cart.RecoveryOwnerAttemptGuid;
        // 中文注释：在离开 UI 线程前固定付款明细，后台 SQLite 不得枚举可能继续变化的界面集合。
        var tenderSnapshot = tenders.ToArray();
        var cartSnapshot = cart.CreateSnapshot();
        var result = checkout.CreatePaymentOrder(cart, session, tenderSnapshot, cashTenderedAmount);
        // 退款代金券先以待发券状态落本地，确保崩溃后仍能沿用原始幂等键恢复。
        var orderForPersistence = PrepareOrderForVoucherRefundPersistence(result.Order);
        var manualReference = tenderSnapshot.FirstOrDefault(tender =>
            tender.Method == PaymentMethodKind.Card && ManualCardPaymentReference.IsManual(tender.Reference))?.Reference;
        if (ManualCardPaymentReference.TryParse(manualReference, out var manualConfirmationId))
        {
            // 中文注释：连首次本地读取失败也必须报告固定订单身份，避免重试转为另一笔订单。
            orderForPersistence = orderForPersistence with { OrderGuid = manualConfirmationId };
        }

        var persistenceOrderGuid = orderForPersistence.OrderGuid;
        // 终端已批准后，订单 GUID 恢复和本地订单落盘不能被随后取消的 UI 操作打断。
        var persistenceCancellationToken = CancellationToken.None;
        LocalOrder order;
        try
        {
            var preparedPersistence = await RunLocalStoreAsync(
                () => PrepareOrderForRecoverableCardPersistenceAsync(
                    orderForPersistence,
                    tenderSnapshot,
                    recoveryOwnerAttemptGuid,
                    recoveryOwnerAttemptKey,
                    persistenceCancellationToken),
                persistenceCancellationToken);
            order = preparedPersistence.Order;
            persistenceOrderGuid = order.OrderGuid;
            if (!preparedPersistence.AlreadyPersisted)
            {
                // 共享挂单取单完成：混合付款路径与 LocalOrder 同一事务写入来源/完成 claim/消费本地挂单。
                var heldOrder = await TryResolveHeldOrderAsync(session, cartSnapshot, persistenceCancellationToken);
                await RunLocalStoreAsync(
                    () => heldOrder is null
                        ? orderRepository.SavePendingOrderAsync(order, persistenceCancellationToken)
                        : orderRepository.SavePendingOrderWithHeldSourceAsync(
                            order,
                            heldOrder,
                            persistenceCancellationToken),
                    persistenceCancellationToken);
            }
        }
        catch (Exception ex) when (
            (recoveryOwnerAttemptGuid is not null ||
             tenderSnapshot.Any(tender => tender.Method == PaymentMethodKind.Card)) &&
            ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 已发布的恢复购物车必须先按精确 attempt 撤回；普通购物车没有 owner，不会被清空。
            RollbackRecoveryPublications(
                cart,
                tenderSnapshot,
                recoveryOwnerAttemptGuid,
                recoveryOwnerAttemptKey);
            if (tenderSnapshot.Any(tender => tender.Method == PaymentMethodKind.Card))
            {
                // 已批准银行卡 tender 代表真实金融副作用；本地订单失败只能进入待恢复，绝不能退回普通付款失败。
                await MarkApprovedCardPersistenceRequiresReviewAsync(tenderSnapshot, ex);
            }

            throw new CardPaymentPersistenceUnknownException(
                persistenceOrderGuid,
                "The card was approved, but POS could not safely save the order. Recovery or supervisor review is required.",
                ex);
        }
        try
        {
            order = await IssuePendingRefundVouchersAsync(order, session, cancellationToken);
        }
        catch (Exception ex) when (IsRetryableFailureAfterOrderPersisted(ex, cancellationToken))
        {
            // 中文注释：退款券签发的致命异常必须原样传播，不能降级成可重试上传失败。
            throw new PaymentUploadFailedException(
                order.OrderGuid,
                CalculateTenderedAmount(tenderSnapshot),
                result.ChangeAmount,
                ex.Message,
                ex);
        }

        result = result with { Order = order };

        var hasPositiveVoucher = result.Order.Payments.Any(payment =>
            payment.Method == Hbpos.Contracts.Orders.PaymentMethodKind.Voucher &&
            payment.Amount > 0m);
        if (hasPositiveVoucher)
        {
            if (orderUploadService is null)
            {
                throw new InvalidOperationException("Voucher payments require online order upload.");
            }

            try
            {
                await orderUploadService.UploadOrderAsync(result.Order.OrderGuid, cancellationToken);
            }
            catch (Exception ex) when (IsRetryableFailureAfterOrderPersisted(ex, cancellationToken))
            {
                // 中文注释：代金券订单上传的致命异常必须原样传播，不能包装成普通上传失败。
                throw new PaymentUploadFailedException(
                    result.Order.OrderGuid,
                    CalculateTenderedAmount(tenderSnapshot),
                    result.ChangeAmount,
                    ex.Message,
                    ex);
            }
        }

        var hasPostCommitWarning = false;
        try
        {
            await MarkCompletedCardAttemptsAsync(
                cart,
                tenderSnapshot,
                recoveryOwnerAttemptGuid,
                recoveryOwnerAttemptKey,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            LogPostCommitWarning("mark-card-attempt-completed", result.Order.OrderGuid, ex);
        }

        try
        {
            await MarkCompletedSquareAttemptsAsync(
                cart,
                tenderSnapshot,
                recoveryOwnerAttemptGuid,
                recoveryOwnerAttemptKey,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            LogPostCommitWarning("mark-square-attempt-completed", result.Order.OrderGuid, ex);
        }

        hasPostCommitWarning |= TryClearCartAfterCommit(cart, result.Order.OrderGuid);

        var pendingSyncResult = await ReadPendingSyncCountAfterCommitAsync(session, cancellationToken);
        hasPostCommitWarning |= pendingSyncResult.HasPostCommitWarning;
        var pendingSyncCount = pendingSyncResult.PendingSyncCount;
        var updatedSession = session with { PendingSyncCount = pendingSyncCount };

        return new CashPaymentWorkflowResult(
            result.Order,
            CalculateTenderedAmount(tenderSnapshot),
            result.ChangeAmount,
            pendingSyncCount,
            updatedSession,
            hasPostCommitWarning);
    }

    public async Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
        Guid orderGuid,
        PosCartService cart,
        PosSessionState session,
        decimal tenderedAmount,
        decimal changeAmount,
        CancellationToken cancellationToken = default)
    {
        // 订单已在首次完成调用中落盘；重试仍必须只收尾当时保留的精确恢复 owner。
        var recoveryOwnerAttemptKey = cart.RecoveryOwnerAttemptKey;
        var recoveryOwnerAttemptGuid = recoveryOwnerAttemptKey?.AttemptGuid ??
            cart.RecoveryOwnerAttemptGuid;
        var order = await RunLocalStoreAsync(
                () => orderRepository.GetOrderAsync(orderGuid, cancellationToken),
                cancellationToken)
            ?? throw new InvalidOperationException("Pending voucher order was not found.");
        try
        {
            order = await IssuePendingRefundVouchersAsync(order, session, cancellationToken);
        }
        catch (Exception ex) when (IsRetryableFailureAfterOrderPersisted(ex, cancellationToken))
        {
            // 中文注释：重试退款券签发同样不拦截 OOM/StackOverflowException。
            throw new PaymentUploadFailedException(
                orderGuid,
                tenderedAmount,
                changeAmount,
                ex.Message,
                ex);
        }

        var hasPositiveVoucher = order.Payments.Any(payment =>
            payment.Method == PaymentMethodKind.Voucher &&
            payment.Amount > 0m);
        if (hasPositiveVoucher)
        {
            if (orderUploadService is null)
            {
                throw new InvalidOperationException("Voucher payments require online order upload.");
            }

            try
            {
                await orderUploadService.UploadOrderAsync(orderGuid, cancellationToken);
            }
            catch (Exception ex) when (IsRetryableFailureAfterOrderPersisted(ex, cancellationToken))
            {
                // 中文注释：重试代金券上传的致命异常必须保持原实例传播。
                throw new PaymentUploadFailedException(
                    orderGuid,
                    tenderedAmount,
                    changeAmount,
                    ex.Message,
                    ex);
            }
        }

        var persistedTenderSnapshot = order.Payments
            .Select(payment => new PaymentTender(
                payment.Method,
                payment.Amount,
                payment.Reference,
                CardTransactions: payment.CardTransactions,
                IdempotencyKey: payment.IdempotencyKey))
            .ToArray();
        var hasPostCommitWarning = false;
        try
        {
            await MarkCompletedCardAttemptsAsync(
                cart,
                persistedTenderSnapshot,
                recoveryOwnerAttemptGuid,
                recoveryOwnerAttemptKey,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            LogPostCommitWarning("retry-voucher-mark-card-attempt-completed", order.OrderGuid, ex);
        }

        try
        {
            await MarkCompletedSquareAttemptsAsync(
                cart,
                persistedTenderSnapshot,
                recoveryOwnerAttemptGuid,
                recoveryOwnerAttemptKey,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            LogPostCommitWarning("retry-voucher-mark-square-attempt-completed", order.OrderGuid, ex);
        }

        hasPostCommitWarning |= TryClearCartAfterCommit(cart, order.OrderGuid);
        var pendingSyncResult = await ReadPendingSyncCountAfterCommitAsync(session, cancellationToken);
        hasPostCommitWarning |= pendingSyncResult.HasPostCommitWarning;
        var pendingSyncCount = pendingSyncResult.PendingSyncCount;
        var updatedSession = session with { PendingSyncCount = pendingSyncCount };

        return new CashPaymentWorkflowResult(
            order,
            tenderedAmount,
            changeAmount,
            pendingSyncCount,
            updatedSession,
            hasPostCommitWarning);
    }

    // 订单落库之后的联网步骤（签发退款券、上传代金券订单）失败时，能否折算成"可用同一订单重试"的上传失败。
    // HttpClient 超时和 API 端点切换都表现为取消异常，而调用方并没有取消；此时订单已经存在、购物车未清，
    // 若原样冒到支付页只会提示"支付未能完成"，收银员再点一次完成就会用新订单号重复落单：
    // 正向代金券会复用同一个预占 token（两单必有一单永远传不上去），退款券会换新幂等键再发一张。
    // 调用方自己发起的取消按惯例原样传播；OOM/StackOverflow 属致命异常，同样不拦截。
    private static bool IsRetryableFailureAfterOrderPersisted(Exception exception, CancellationToken callerCancellationToken)
    {
        return exception switch
        {
            OutOfMemoryException or StackOverflowException => false,
            OperationCanceledException => !callerCancellationToken.IsCancellationRequested,
            _ => true
        };
    }

    private async Task<(int PendingSyncCount, bool HasPostCommitWarning)> ReadPendingSyncCountAfterCommitAsync(
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await RunLocalStoreAsync(
                () => syncQueueRepository.CountPendingAsync(cancellationToken),
                cancellationToken), false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogPostCommitWarning("refresh-pending-sync", orderGuid: null, ex);
            return (session.PendingSyncCount, true);
        }
    }

    private static void LogPostCommitWarning(string stage, Guid? orderGuid, Exception ex)
    {
        // 订单已落库后的收尾步骤失败：不回滚订单，但必须以 Warning 留痕并带异常，便于按订单号追查。
        ConsoleLog.WriteWarning(
            "CashPaymentWorkflow",
            $"post-commit warning stage={stage} orderGuid={orderGuid?.ToString("D") ?? "<none>"} error={ex.GetType().Name}",
            new ApplicationLogContext(
                TraceId: orderGuid?.ToString("D"),
                Properties: new Dictionary<string, object?>
                {
                    ["phase"] = stage
                }),
            ex);
    }

    private async Task<LocalHeldOrderCompletionContext?> TryResolveHeldOrderAsync(
        PosSessionState session,
        PosCartSnapshot cartSnapshot,
        CancellationToken cancellationToken)
    {
        if (_heldOrderPaymentSourceResolver is null)
        {
            return null;
        }

        return await _heldOrderPaymentSourceResolver.TryResolveAsync(
            session,
            cartSnapshot,
            cancellationToken);
    }

    private static Task RunLocalStoreAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        // 中文注释：终端取消后仍要完成已开始的本地金融日志，不能让已取消的 UI token 跳过审计状态落盘。
        var workerCancellationToken = GetLocalPersistenceCancellationToken(cancellationToken);
        return Task.Run(operation, workerCancellationToken);
    }

    private static Task<T> RunLocalStoreAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        // 中文注释：调用方必须先构造不可变订单或 attempt 快照，后台委托不得访问购物车或界面集合。
        var workerCancellationToken = GetLocalPersistenceCancellationToken(cancellationToken);
        return Task.Run(operation, workerCancellationToken);
    }

    private static CancellationToken GetLocalPersistenceCancellationToken(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;

    private static bool TryClearCartAfterCommit(PosCartService cart, Guid orderGuid)
    {
        try
        {
            cart.Clear();
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 订单已持久化后，购物车通知失败只能作为收尾告警，不能把已完成收款变成可重付失败。
            LogPostCommitWarning("clear-cart", orderGuid, ex);
            return true;
        }
    }

    private async Task<PaymentTenderAttemptResult> AuthorizeCardTenderAsync(
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        PosCartSnapshot? cartSnapshot,
        string? referenceText,
        CancellationToken cancellationToken,
        bool isRefund,
        string exceedsRemainingStatusKey,
        string declinedStatusKey,
        string approvedStatusKey)
    {
        if (amount > remainingAmount)
        {
            ConsoleLog.Write(
                "CardRefund",
                $"workflow blocked card {(isRefund ? "refund" : "payment")} reason=amount-exceeds-remaining amount={amount:0.00} remaining={remainingAmount:0.00} originalReference={LogValue(referenceText)}");
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        if (!isRefund && amount < remainingAmount)
        {
            ConsoleLog.Write(
                "CardRefund",
                $"workflow blocked card payment reason=amount-below-remaining amount={amount:0.00} remaining={remainingAmount:0.00}");
            return PaymentTenderAttemptResult.Fail("payment.status.cardMustBeFinalTender");
        }

        var operation = isRefund ? "refund" : "payment";
        ConsoleLog.Write(
            "CardRefund",
            $"workflow terminal {operation} start amount={amount:0.00} remaining={remainingAmount:0.00} originalReference={LogValue(referenceText)}");

        // 中文注释：attempt 草稿包含当前付款明细的不可变副本，后台 SQLite 不能直接读取 UI 绑定集合。
        var tenderSnapshot = currentTenders.ToArray();
        var persistenceCancellationToken = GetLocalPersistenceCancellationToken(cancellationToken);
        var settingsSnapshot = cardTerminalSettingsProvider is null
            ? null
            : await RunLocalStoreAsync(
                () => cardTerminalSettingsProvider.GetSettingsAsync(persistenceCancellationToken),
                persistenceCancellationToken);
        (LocalCardPaymentAttempt? Attempt, bool Reused, bool RequiresRecovery) cardAttemptSelection =
            (null, false, false);
        (LocalSquarePaymentAttempt? Attempt, bool Reused) squareAttemptSelection = (null, false);
        if (settingsSnapshot?.Processor == CardProcessorKind.Linkly)
        {
            cardAttemptSelection = await RunLocalStoreAsync(
                () => TryCreateLinklyPaymentAttemptAsync(
                    settingsSnapshot,
                    amount,
                    session,
                    actualAmount,
                    tenderSnapshot,
                    cartSnapshot,
                    referenceText,
                    isRefund,
                    persistenceCancellationToken),
                persistenceCancellationToken);
        }
        else if (settingsSnapshot?.Processor == CardProcessorKind.Square)
        {
            squareAttemptSelection = await RunLocalStoreAsync(
                () => TryCreateSquarePaymentAttemptAsync(
                    settingsSnapshot,
                    amount,
                    session,
                    actualAmount,
                    tenderSnapshot,
                    cartSnapshot,
                    referenceText,
                    isRefund,
                    persistenceCancellationToken),
                persistenceCancellationToken);
        }

        var attempt = cardAttemptSelection.Attempt;
        var squareAttempt = squareAttemptSelection.Attempt;

        if (isRefund)
        {
            var existingLinklyAttemptRequiresRecovery = cardAttemptSelection.RequiresRecovery ||
                (cardAttemptSelection.Reused &&
                 attempt is not null &&
                 (attempt.Status != LocalCardPaymentAttemptStatus.Pending ||
                  !string.IsNullOrWhiteSpace(attempt.SessionId)));
            var existingSquareAttemptRequiresRecovery = squareAttemptSelection.Reused &&
                squareAttempt is not null &&
                (squareAttempt.Status != LocalSquarePaymentAttemptStatus.Pending ||
                 !string.IsNullOrWhiteSpace(squareAttempt.CheckoutId) ||
                 !string.IsNullOrWhiteSpace(squareAttempt.PaymentId));
            if (existingLinklyAttemptRequiresRecovery || existingSquareAttemptRequiresRecovery)
            {
                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    "A matching card refund is already in progress or requires supervisor reconciliation.",
                    recoveryAttempt: attempt,
                    squareRecoveryAttempt: squareAttempt);
            }

            var durableRefundConfigured = cardTerminalSettingsProvider is not null &&
                (cardPaymentAttemptRepository is not null || squarePaymentAttemptRepository is not null);
            if (durableRefundConfigured && attempt is null && squareAttempt is null)
            {
                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    "The card refund attempt could not be persisted, so the terminal was not called.");
            }
        }

        PaymentAuthorizationResult authorization;
        var linklySubmissionObserved = false;
        var squareSubmissionObserved = false;
        var refundDispatchBoundaryPersisted = false;
        string? refundIdempotencyKey = null;
        string? refundSubmissionToken = null;
        if (isRefund && (attempt is not null || squareAttempt is not null))
        {
            refundIdempotencyKey = attempt?.TxnRef ?? squareAttempt?.IdempotencyKey;
            if (string.IsNullOrWhiteSpace(refundIdempotencyKey) ||
                _cardTerminalClient is not IIdempotentCardRefundClient)
            {
                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    "The card refund adapter cannot safely reuse the persisted attempt.",
                    recoveryAttempt: attempt,
                    squareRecoveryAttempt: squareAttempt);
            }

            try
            {
                // 中文注释：提交令牌既是跨进程 claim，也是旧 worker 的 fencing token；只有 CAS 赢家可以调用终端。
                refundSubmissionToken = Guid.NewGuid().ToString("N");
                if (attempt is not null)
                {
                    var boundaryAt = NextAttemptTimestamp(attempt.UpdatedAt);
                    var claimed = await RunLocalStoreAsync(
                        () => cardPaymentAttemptRepository!.TryBeginRefundSubmissionAsync(
                            attempt.AttemptGuid,
                            attempt.UpdatedAt,
                            refundSubmissionToken,
                            boundaryAt,
                            CancellationToken.None),
                        CancellationToken.None);
                    if (!claimed)
                    {
                        return CreateCardFailureResult(
                            "payment.card.resultUnknown",
                            "The card refund attempt is already being processed or was reconciled.",
                            recoveryAttempt: attempt);
                    }

                    attempt = attempt with
                    {
                        Status = LocalCardPaymentAttemptStatus.Recovering,
                        ResponseCode = null,
                        ResponseText = null,
                        SubmissionToken = refundSubmissionToken,
                        UpdatedAt = boundaryAt
                    };
                }

                if (squareAttempt is not null)
                {
                    var boundaryAt = NextAttemptTimestamp(squareAttempt.UpdatedAt);
                    var claimed = await RunLocalStoreAsync(
                        () => squarePaymentAttemptRepository!.TryBeginRefundSubmissionAsync(
                            squareAttempt.AttemptGuid,
                            squareAttempt.UpdatedAt,
                            refundSubmissionToken,
                            boundaryAt,
                            CancellationToken.None),
                        CancellationToken.None);
                    if (!claimed)
                    {
                        return CreateCardFailureResult(
                            "payment.card.resultUnknown",
                            "The card refund attempt is already being processed or was reconciled.",
                            squareRecoveryAttempt: squareAttempt);
                    }

                    squareAttempt = squareAttempt with
                    {
                        Status = LocalSquarePaymentAttemptStatus.Recovering,
                        ResponseCode = null,
                        ResponseText = null,
                        SubmissionToken = refundSubmissionToken,
                        UpdatedAt = boundaryAt
                    };
                }

                refundDispatchBoundaryPersisted = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LogCardRecoveryWarning("persist-refund-dispatch-boundary", attempt?.AttemptGuid ?? squareAttempt?.AttemptGuid, ex);
                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    "The card refund attempt could not be locked before terminal submission.");
            }
        }

        using var linklyAttemptScope = attempt is null || linklyPaymentAttemptContextAccessor is null
            ? null
            : linklyPaymentAttemptContextAccessor.Begin(new LinklyPaymentAttemptContext(
                attempt.AttemptGuid,
                (sessionId, txnRef, updatedAt, _) =>
                {
                    // 已收到 Linkly session 表明终端已接单；本地提交证据必须脱离 UI 取消。
                    linklySubmissionObserved = true;
                    return RunLocalStoreAsync(
                        () => refundSubmissionToken is not null
                            ? RequireRefundCasAsync(
                                cardPaymentAttemptRepository!.TryUpdateRefundSessionAsync(
                                    attempt.AttemptGuid,
                                    refundSubmissionToken,
                                    sessionId,
                                    txnRef,
                                    updatedAt,
                                    CancellationToken.None))
                            : cardPaymentAttemptRepository!.UpdateSessionAsync(
                                attempt.AttemptGuid,
                                sessionId,
                                txnRef,
                                updatedAt,
                                CancellationToken.None),
                        CancellationToken.None);
                },
                attempt.TxnRef,
                refundSubmissionToken,
                TakeOverActiveSessionAsync: (settings, activeStatus, cancellationToken) =>
                    TakeOverActiveSessionForNewPaymentAsync(
                        settings,
                        activeStatus,
                        session,
                        cancellationToken))
            {
                SettingsSnapshot = settingsSnapshot
            });
        using var squareAttemptScope = squareAttempt is null || squarePaymentAttemptContextAccessor is null
            ? null
            : squarePaymentAttemptContextAccessor.Begin(new SquarePaymentAttemptContext(
                squareAttempt.AttemptGuid,
                squareAttempt.IdempotencyKey,
                (checkoutId, checkoutStatus, updatedAt, _) =>
                {
                    // Square 返回 checkoutId 即已提交；状态证据必须脱离 UI 取消。
                    squareSubmissionObserved = true;
                    return RunLocalStoreAsync(
                        () => !string.IsNullOrWhiteSpace(squareAttempt.SubmissionToken)
                            ? RequireRefundCasAsync(
                                squarePaymentAttemptRepository!.TryMarkCheckoutCreatedAsync(
                                    squareAttempt.AttemptGuid,
                                    squareAttempt.SubmissionToken!,
                                    checkoutId,
                                    checkoutStatus,
                                    updatedAt,
                                    CancellationToken.None))
                            : squarePaymentAttemptRepository!.MarkCheckoutCreatedAsync(
                                squareAttempt.AttemptGuid,
                                checkoutId,
                                checkoutStatus,
                                updatedAt,
                                CancellationToken.None),
                        CancellationToken.None);
                },
                squareAttempt.SubmissionToken,
                (refundId, refundStatus, updatedAt, _) =>
                {
                    // Square 已返回 refundId 即代表退款已受理；必须先持久化，重启后只能查询，不能重复 POST。
                    squareSubmissionObserved = true;
                    return RunLocalStoreAsync(
                        () => !string.IsNullOrWhiteSpace(squareAttempt.SubmissionToken)
                            ? RequireRefundCasAsync(
                                TryRecordCurrentSquareRefundResponseAsync(
                                    squareAttempt.AttemptGuid,
                                    squareAttempt.SubmissionToken!,
                                    refundId,
                                    refundStatus,
                                    updatedAt,
                                    CancellationToken.None))
                            : Task.FromException(
                                new InvalidOperationException("Square 退款缺少 submission token，无法持久化退款响应。")),
                        CancellationToken.None);
                }));
        try
        {
            if (settingsSnapshot is not null &&
                _cardTerminalClient is ICardTerminalSettingsBoundClient settingsBoundClient)
            {
                authorization = isRefund
                    ? await settingsBoundClient.RefundWithSettingsAsync(
                        settingsSnapshot,
                        amount,
                        session,
                        referenceText,
                        refundIdempotencyKey,
                        cancellationToken)
                    : await settingsBoundClient.AuthorizeWithSettingsAsync(
                        settingsSnapshot,
                        amount,
                        session,
                        cancellationToken);
            }
            else
            {
                authorization = isRefund && refundIdempotencyKey is not null
                    ? await ((IIdempotentCardRefundClient)_cardTerminalClient).RefundAsync(
                        amount,
                        session,
                        referenceText,
                        refundIdempotencyKey,
                        cancellationToken)
                    : isRefund
                        ? await _cardTerminalClient.RefundAsync(amount, session, referenceText, cancellationToken)
                        : await _cardTerminalClient.AuthorizeAsync(amount, session, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var definitelyNotSubmitted = ex is CardTerminalNotSubmittedException;
            LocalCardPaymentAttempt? linklyAttemptAfterException;
            LocalSquarePaymentAttempt? squareAttemptAfterException;
            try
            {
                linklyAttemptAfterException = attempt is null
                    ? null
                    : await RunLocalStoreAsync(
                        () => cardPaymentAttemptRepository!.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                        CancellationToken.None);
                squareAttemptAfterException = squareAttempt is null
                    ? null
                    : await RunLocalStoreAsync(
                        () => squarePaymentAttemptRepository!.GetAttemptAsync(squareAttempt.AttemptGuid, CancellationToken.None),
                        CancellationToken.None);
            }
            catch (Exception readException) when (readException is not OutOfMemoryException and not StackOverflowException)
            {
                LogCardRecoveryWarning("read-attempt", attempt?.AttemptGuid ?? squareAttempt?.AttemptGuid, readException);
                if (definitelyNotSubmitted && cancellationToken.IsCancellationRequested)
                {
                    return CreateCardFailureResult("payment.status.cardCancelled");
                }

                // 未收到“提交前取消”信号且无法可靠读取提交边界时，必须保守锁定。
                return CreateCardFailureResult("payment.card.resultUnknown");
            }

            // LocalIp 在 socket 写入前已持久化 TxnRef；没有 SessionId 也不能把其取消当作未提交。
            // 后端异步模式的销售引用则在建 attempt 时就已派生落库（远早于发请求），它的存在不说明终端是否接单：
            // 该模式由终端客户端掌握提交边界——POST 之前失败会返回可回退结果或抛 CardTerminalNotSubmittedException，
            // POST 之后失败会返回未知结果；异常走到这里时以会话是否已绑定为准。退款在各模式下维持原有的保守判定。
            var txnRefMarksDispatch = isRefund || !IsCloudBackendAsyncAttempt(linklyAttemptAfterException);
            var wasSubmitted = !definitelyNotSubmitted && (
                linklySubmissionObserved ||
                squareSubmissionObserved ||
                refundDispatchBoundaryPersisted ||
                !string.IsNullOrWhiteSpace(linklyAttemptAfterException?.SessionId) ||
                (txnRefMarksDispatch && !string.IsNullOrWhiteSpace(linklyAttemptAfterException?.TxnRef)) ||
                !string.IsNullOrWhiteSpace(squareAttemptAfterException?.CheckoutId));

            if (wasSubmitted)
            {
                // 终端已接单后任何异常都不能被当成失败并允许重新收款。
                var recoveryPersistenceSucceeded = true;
                try
                {
                    if (linklyAttemptAfterException is not null &&
                        (!string.IsNullOrWhiteSpace(linklyAttemptAfterException.SessionId) ||
                         !string.IsNullOrWhiteSpace(linklyAttemptAfterException.TxnRef)))
                    {
                        await RunLocalStoreAsync(
                            () => refundSubmissionToken is not null
                                ? RequireRefundCasAsync(
                                    cardPaymentAttemptRepository!.TryMarkRefundRecoveringAsync(
                                        linklyAttemptAfterException.AttemptGuid,
                                        refundSubmissionToken,
                                        DateTimeOffset.UtcNow,
                                        CancellationToken.None))
                                : MarkCurrentCardAttemptRecoveringWithCasAsync(
                                    linklyAttemptAfterException.AttemptGuid,
                                    CancellationToken.None),
                            CancellationToken.None);
                    }

                    if (squareAttemptAfterException is not null &&
                        (isRefund ||
                         squareSubmissionObserved ||
                         !string.IsNullOrWhiteSpace(squareAttemptAfterException.CheckoutId)) &&
                        string.Equals(
                            squareAttemptAfterException.SubmissionToken,
                            squareAttempt?.SubmissionToken,
                            StringComparison.Ordinal))
                    {
                        await RunLocalStoreAsync(
                            () => refundSubmissionToken is not null
                                ? RequireRefundCasAsync(
                                    TryMarkCurrentSquareRefundFailedAsync(
                                        squareAttemptAfterException.AttemptGuid,
                                        refundSubmissionToken,
                                        LocalSquarePaymentAttemptStatus.Unknown,
                                        squareAttemptAfterException.CheckoutStatus,
                                        squareAttemptAfterException.PaymentStatus,
                                        squareAttemptAfterException.ResponseCode,
                                        "Square checkout ended with an exception after submission; result requires recovery.",
                                        DateTimeOffset.UtcNow,
                                        CancellationToken.None,
                                        squareAttemptAfterException.CancelReason))
                                : squarePaymentAttemptRepository!.MarkFailedAsync(
                                    squareAttemptAfterException.AttemptGuid,
                                    LocalSquarePaymentAttemptStatus.Unknown,
                                    squareAttemptAfterException.CheckoutStatus,
                                    squareAttemptAfterException.PaymentStatus,
                                    squareAttemptAfterException.ResponseCode,
                                    "Square checkout ended with an exception after submission; result requires recovery.",
                                    DateTimeOffset.UtcNow,
                                    CancellationToken.None,
                                    squareAttemptAfterException.CancelReason),
                            CancellationToken.None);
                    }
                }
                catch (Exception persistException) when (persistException is not OutOfMemoryException and not StackOverflowException)
                {
                    recoveryPersistenceSucceeded = false;
                    LogCardRecoveryWarning("persist-recovery", attempt?.AttemptGuid ?? squareAttempt?.AttemptGuid, persistException);
                }

                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    recoveryAttempt: recoveryPersistenceSucceeded ? linklyAttemptAfterException : null,
                    squareRecoveryAttempt: recoveryPersistenceSucceeded ? squareAttemptAfterException : null);
            }

            try
            {
                if (attempt is not null)
                {
                    var attemptStatus = ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                        ? LocalCardPaymentAttemptStatus.Cancelled
                        : LocalCardPaymentAttemptStatus.Failed;
                    var attemptMessage = ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                        ? "Card payment was canceled before terminal submission."
                        : "Card terminal request failed before terminal submission.";
                    await RunLocalStoreAsync(
                        () => refundSubmissionToken is not null
                            ? RequireRefundCasAsync(
                                cardPaymentAttemptRepository!.TryUpdateRefundOutcomeAsync(
                                    attempt.AttemptGuid,
                                    refundSubmissionToken,
                                    attemptStatus,
                                    null,
                                    attemptMessage,
                                    null,
                                    DateTimeOffset.UtcNow,
                                    CancellationToken.None))
                            : cardPaymentAttemptRepository!.UpdateOutcomeAsync(
                                attempt.AttemptGuid,
                                attemptStatus,
                                null,
                                attemptMessage,
                                null,
                                DateTimeOffset.UtcNow,
                                CancellationToken.None),
                        CancellationToken.None);
                }

                if (squareAttempt is not null)
                {
                    var squareStatus = ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                        ? LocalSquarePaymentAttemptStatus.Canceled
                        : LocalSquarePaymentAttemptStatus.Failed;
                    var squareMessage = ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                        ? "Card payment was canceled before terminal submission."
                        : "Card terminal request failed before terminal submission.";
                    await RunLocalStoreAsync(
                        () => refundSubmissionToken is not null
                            ? RequireRefundCasAsync(
                                TryMarkCurrentSquareRefundFailedAsync(
                                    squareAttempt.AttemptGuid,
                                    refundSubmissionToken,
                                    squareStatus,
                                    null,
                                    null,
                                    null,
                                    squareMessage,
                                    DateTimeOffset.UtcNow,
                                    CancellationToken.None,
                                    cancelReason: null))
                            : squarePaymentAttemptRepository!.MarkFailedAsync(
                                squareAttempt.AttemptGuid,
                                squareStatus,
                                null,
                                null,
                                null,
                                squareMessage,
                                DateTimeOffset.UtcNow,
                                CancellationToken.None),
                        CancellationToken.None);
                }
            }
            catch (Exception persistException) when (persistException is not OutOfMemoryException and not StackOverflowException)
            {
                LogCardRecoveryWarning("persist-failure", attempt?.AttemptGuid ?? squareAttempt?.AttemptGuid, persistException);
                if (definitelyNotSubmitted && cancellationToken.IsCancellationRequested)
                {
                    return CreateCardFailureResult("payment.status.cardCancelled");
                }

                return CreateCardFailureResult("payment.card.resultUnknown");
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                return CreateCardFailureResult("payment.status.cardCancelled");
            }

            throw;
        }

        if (squareAttempt is not null && !authorization.Approved && !authorization.ResultUnknown)
        {
            LocalSquarePaymentAttempt? squareAttemptAfterAuthorization;
            try
            {
                squareAttemptAfterAuthorization = await RunLocalStoreAsync(
                    () => squarePaymentAttemptRepository!.GetAttemptAsync(squareAttempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LogCardRecoveryWarning("read-square-attempt", squareAttempt.AttemptGuid, ex);
                return CreateCardFailureResult("payment.card.resultUnknown");
            }

            if (isRefund &&
                refundSubmissionToken is not null &&
                squareAttemptAfterAuthorization is not null &&
                IsTerminalSquareRefundFailure(squareAttemptAfterAuthorization.PaymentStatus))
            {
                const string squareRefundFailureMessage =
                    "Square confirmed that the refund failed. Complete recovery before selecting an alternative refund method.";
                try
                {
                    // Square 已经给出退款终态时，必须先把失败证据移交给可重启的 FinalizePending 队列。
                    var recoveryAttempt = await PersistSquareRefundFailureHandoffAsync(
                        squareAttemptAfterAuthorization,
                        refundSubmissionToken,
                        authorization,
                        CancellationToken.None);
                    if (recoveryAttempt is null)
                    {
                        // CAS 输家只能返回重读后仍位于开放队列的真实 winner；终态或换 token 的
                        // attempt 不能伪装成可恢复身份，否则付款页会拿到异常中心永远查不到的 key。
                        return CreateCardFailureResult(
                            "payment.card.resultUnknown",
                            "Square refund evidence changed while the recovery handoff was being persisted. Supervisor reconciliation is required.");
                    }

                    return CreateCardFailureResult(
                        "payment.card.resultUnknown",
                        squareRefundFailureMessage,
                        squareRecoveryAttempt: recoveryAttempt);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LogCardRecoveryWarning("persist-square-refund-failure-finalization", squareAttempt.AttemptGuid, ex);
                    // 无法重读并验证开放 winner 时必须失败关闭，不能退回 pre-CAS 快照伪造恢复身份。
                    return CreateCardFailureResult(
                        "payment.card.resultUnknown",
                        "Square refund failure could not be durably handed off. Supervisor reconciliation is required.");
                }
            }

            var hasUnresolvedSquareAttempt = squareAttemptAfterAuthorization?.Status == LocalSquarePaymentAttemptStatus.Unknown ||
                (squareAttemptAfterAuthorization?.Status is not (
                    LocalSquarePaymentAttemptStatus.Canceled or
                    LocalSquarePaymentAttemptStatus.Failed or
                    LocalSquarePaymentAttemptStatus.TimedOut or
                    LocalSquarePaymentAttemptStatus.Abandoned or
                    LocalSquarePaymentAttemptStatus.OrderCompleted) &&
                 !string.IsNullOrWhiteSpace(squareAttemptAfterAuthorization?.CheckoutId));
            if (hasUnresolvedSquareAttempt)
            {
                const string squareResultUnknownMessage = "Square checkout result could not be confirmed. Recovery is required.";
                var squareRecoveryPersisted = false;
                try
                {
                    await MarkSquareAttemptRequiresRecoveryAsync(
                        squareAttempt,
                        authorization,
                        squareResultUnknownMessage,
                        refundSubmissionToken);
                    squareRecoveryPersisted = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LogCardRecoveryWarning("persist-square-recovery", squareAttempt.AttemptGuid, ex);
                }

                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    squareResultUnknownMessage,
                    squareRecoveryAttempt: squareRecoveryPersisted ? squareAttempt : null);
            }

            await RunLocalStoreAsync(
                () => refundSubmissionToken is not null
                    ? RequireRefundCasAsync(
                        TryMarkCurrentSquareRefundFailedAsync(
                            squareAttempt.AttemptGuid,
                            refundSubmissionToken,
                            MapSquareAuthorizationFailureStatus(authorization.StatusKey, authorization.Message),
                            null,
                            authorization.ResponseText,
                            authorization.ResponseCode,
                            authorization.Message,
                            DateTimeOffset.UtcNow,
                            CancellationToken.None))
                    : squarePaymentAttemptRepository!.MarkFailedAsync(
                        squareAttempt.AttemptGuid,
                        MapSquareAuthorizationFailureStatus(authorization.StatusKey, authorization.Message),
                        null,
                        authorization.ResponseText,
                        authorization.ResponseCode,
                        authorization.Message,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None),
                CancellationToken.None);
        }

        ConsoleLog.Write(
            "CardRefund",
            $"workflow terminal {operation} completed approved={authorization.Approved} reference={LogValue(authorization.Reference)} " +
            $"message={LogValue(authorization.Message)} authorizedAmount={authorization.AuthorizedAmount?.ToString("0.00") ?? "<null>"} " +
            $"cardTxCount={authorization.CardTransactions?.Count ?? 0}");

        if (authorization.ResultUnknown)
        {
            const string resultUnknownMessage = "Card terminal result could not be confirmed. Recovery is required.";
            LocalCardPaymentAttempt? persistedLinklyRecovery = null;
            LocalSquarePaymentAttempt? persistedSquareRecovery = null;
            try
            {
                if (attempt is not null)
                {
                    if (await TryPersistCardPaymentAttemptAfterFinancialResultAsync(
                        attempt.AttemptGuid,
                        authorization,
                        CancellationToken.None,
                        refundSubmissionToken: refundSubmissionToken))
                    {
                        persistedLinklyRecovery = attempt;
                    }
                }

                await MarkSquareAttemptRequiresRecoveryAsync(
                    squareAttempt,
                    authorization,
                    resultUnknownMessage,
                    refundSubmissionToken);
                persistedSquareRecovery = squareAttempt;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LogCardRecoveryWarning("persist-result-unknown", attempt?.AttemptGuid ?? squareAttempt?.AttemptGuid, ex);
            }

            return CreateCardFailureResult(
                "payment.card.resultUnknown",
                resultUnknownMessage,
                recoveryAttempt: persistedLinklyRecovery,
                squareRecoveryAttempt: persistedSquareRecovery);
        }

        if (!authorization.Approved)
        {
            if (attempt is not null)
            {
                try
                {
                    await UpdateCardPaymentAttemptAfterAuthorizationAsync(
                        attempt.AttemptGuid,
                        authorization,
                        CancellationToken.None,
                        refundSubmissionToken: refundSubmissionToken);
                    // 明确的取消或拒绝已经耐久落库后才能释放云端 session，避免终端被未确认终态长期占用。
                    await AcknowledgeCompletedCardAttemptAsync(
                        attempt.AttemptGuid,
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    // 接管旧 session 失败时本次新交易确定未提交；本地终态写失败也不能伪造 ResultUnknown 锁。
                    LogCardRecoveryWarning("persist-definitive-not-submitted", attempt.AttemptGuid, ex);
                }
            }

            return CreateCardFailureResult(
                string.IsNullOrWhiteSpace(authorization.StatusKey) ? declinedStatusKey : authorization.StatusKey,
                authorization.Message,
                isTerminalDecline: HasTerminalDeclineEvidence(authorization));
        }

        var authorizedAmount = decimal.Round(
            authorization.AuthorizedAmount ?? amount,
            2,
            MidpointRounding.AwayFromZero);
        if (authorizedAmount <= 0m)
        {
            const string nonPositiveAmountMessage = "Card terminal approved a non-positive amount. Supervisor review is required.";
            LocalCardPaymentAttempt? persistedLinklyRecovery = null;
            if (attempt is not null)
            {
                if (!await TryPersistCardPaymentAttemptAfterFinancialResultAsync(
                    attempt.AttemptGuid,
                    authorization,
                    CancellationToken.None,
                    LocalCardPaymentAttemptStatus.RequiresReview,
                    nonPositiveAmountMessage,
                    refundSubmissionToken))
                {
                    return CreateCardFailureResult("payment.card.resultUnknown", nonPositiveAmountMessage);
                }

                persistedLinklyRecovery = attempt;
            }

            await MarkSquareAttemptRequiresRecoveryAsync(
                squareAttempt,
                authorization,
                nonPositiveAmountMessage,
                refundSubmissionToken);
            return CreateCardFailureResult(
                "payment.card.resultUnknown",
                nonPositiveAmountMessage,
                recoveryAttempt: persistedLinklyRecovery,
                squareRecoveryAttempt: squareAttempt);
        }

        if (authorizedAmount > remainingAmount)
        {
            const string exceedsRemainingMessage = "Card terminal authorized amount exceeded the remaining amount.";
            LocalCardPaymentAttempt? persistedLinklyRecovery = null;
            if (attempt is not null)
            {
                if (!await TryPersistCardPaymentAttemptAfterFinancialResultAsync(
                    attempt.AttemptGuid,
                    authorization,
                    CancellationToken.None,
                    LocalCardPaymentAttemptStatus.RequiresReview,
                    exceedsRemainingMessage,
                    refundSubmissionToken))
                {
                    return CreateCardFailureResult("payment.card.resultUnknown", exceedsRemainingMessage);
                }

                persistedLinklyRecovery = attempt;
            }

            await MarkSquareAttemptRequiresRecoveryAsync(
                squareAttempt,
                authorization,
                exceedsRemainingMessage,
                refundSubmissionToken);
            return CreateCardFailureResult(
                "payment.card.resultUnknown",
                exceedsRemainingMessage,
                recoveryAttempt: persistedLinklyRecovery,
                squareRecoveryAttempt: squareAttempt);
        }

        if (authorizedAmount != amount)
        {
            const string amountMismatchMessage = "Card terminal authorized amount did not match the requested amount.";
            LocalCardPaymentAttempt? persistedLinklyRecovery = null;
            if (attempt is not null)
            {
                if (!await TryPersistCardPaymentAttemptAfterFinancialResultAsync(
                    attempt.AttemptGuid,
                    authorization,
                    CancellationToken.None,
                    LocalCardPaymentAttemptStatus.RequiresReview,
                    amountMismatchMessage,
                    refundSubmissionToken))
                {
                    return CreateCardFailureResult("payment.card.resultUnknown", amountMismatchMessage);
                }

                persistedLinklyRecovery = attempt;
            }

            await MarkSquareAttemptRequiresRecoveryAsync(
                squareAttempt,
                authorization,
                amountMismatchMessage,
                refundSubmissionToken);
            return CreateCardFailureResult(
                "payment.card.resultUnknown",
                amountMismatchMessage,
                recoveryAttempt: persistedLinklyRecovery,
                squareRecoveryAttempt: squareAttempt);
        }

        if (attempt is not null)
        {
            if (!await TryPersistCardPaymentAttemptAfterFinancialResultAsync(
                attempt.AttemptGuid,
                authorization,
                CancellationToken.None,
                refundSubmissionToken: refundSubmissionToken))
            {
                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    "The card was approved but the local recovery record could not be confirmed.");
            }
        }

        if (squareAttempt is not null && refundSubmissionToken is not null)
        {
            var squareRefundTransaction = authorization.CardTransactions?.FirstOrDefault(transaction =>
                string.Equals(transaction.Processor, "Square", StringComparison.OrdinalIgnoreCase));
            var paymentId = squareRefundTransaction?.TxnRef;
            if (string.IsNullOrWhiteSpace(paymentId) &&
                authorization.Reference?.StartsWith("SQRF:", StringComparison.OrdinalIgnoreCase) == true)
            {
                paymentId = authorization.Reference["SQRF:".Length..];
            }

            var paymentStatus = squareRefundTransaction?.ResponseText ?? authorization.Message;
            if (string.IsNullOrWhiteSpace(paymentId) || string.IsNullOrWhiteSpace(paymentStatus))
            {
                const string missingSquareEvidenceMessage =
                    "Square approved the refund but did not return durable refund evidence. Recovery is required.";
                await MarkSquareAttemptRequiresRecoveryAsync(
                    squareAttempt,
                    authorization,
                    missingSquareEvidenceMessage,
                    refundSubmissionToken);
                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    missingSquareEvidenceMessage,
                    squareRecoveryAttempt: squareAttempt);
            }

            try
            {
                // 退款成功必须由当前 submission token 落库；旧 worker 迟到时不得生成成功 tender。
                await RunLocalStoreAsync(
                    () => RequireRefundCasAsync(
                        TryMarkCurrentSquareRefundPaymentVerifiedAsync(
                            squareAttempt.AttemptGuid,
                            refundSubmissionToken,
                            paymentId,
                            paymentStatus,
                            authorization.ResponseCode,
                            "Refund verified.",
                            DateTimeOffset.UtcNow,
                            CancellationToken.None)),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LogCardRecoveryWarning("persist-square-refund-approved", squareAttempt.AttemptGuid, ex);
                return CreateCardFailureResult(
                    "payment.card.resultUnknown",
                    "The Square refund was approved but its current attempt could not be confirmed. Recovery is required.");
            }
        }

        var reference = isRefund
            ? CardRefundReference.Format(authorization.Reference, referenceText!)
            : authorization.Reference;
        // Square 退款已受理但仍在结算（PENDING）：退货照常完成，付款页提示“已受理、无需刷卡、通常几小时内完成”。
        var isAcceptedPendingSquareRefund = isRefund &&
            string.Equals(authorization.StatusKey, ConfiguredCardTerminalClient.SquareRefundAcceptedStatusKey, StringComparison.Ordinal);
        var successStatusKey = authorization.FallbackSucceeded
            ? "payment.linklyFallback.succeeded"
            : isAcceptedPendingSquareRefund
                ? ConfiguredCardTerminalClient.SquareRefundAcceptedStatusKey
                : approvedStatusKey;
        var successStatusMessage = authorization.FallbackSucceeded
            ? string.Format(
                CultureInfo.CurrentCulture,
                T("payment.linklyFallback.succeeded"),
                FormatLinklyModeDisplayName(authorization.RequestedConnectionMode),
                FormatLinklyModeDisplayName(authorization.ActualConnectionMode),
                T("payment.linklyFallback.promotePrimary"))
            : isAcceptedPendingSquareRefund
                ? authorization.Message
                : null;

        return PaymentTenderAttemptResult.Success(
            new PaymentTender(
                PaymentMethodKind.Card,
                isRefund ? -authorizedAmount : authorizedAmount,
                reference,
                CardTransactions: authorization.CardTransactions,
                IdempotencyKey: attempt is not null
                    ? FormatCardAttemptTenderKey(attempt.AttemptGuid)
                    : squareAttempt is not null
                        ? FormatSquareAttemptTenderKey(squareAttempt.AttemptGuid)
                        : null),
            successStatusKey,
            successStatusMessage);
    }

    private PaymentTenderAttemptResult CreateCardFailureResult(
        string statusKey,
        string? statusMessage = null,
        bool isTerminalDecline = false,
        LocalCardPaymentAttempt? recoveryAttempt = null,
        LocalSquarePaymentAttempt? squareRecoveryAttempt = null)
    {
        var result = _cardPaymentResultPolicyResolver.Apply(
            PaymentTenderAttemptResult.Fail(statusKey, statusMessage, isTerminalDecline));
        if (TryCreateRecoveryIdentity(recoveryAttempt, squareRecoveryAttempt, out var key, out var orderGuid))
        {
            result = result with
            {
                RecoveryAttemptKey = key,
                RecoveryOrderGuid = orderGuid
            };
        }

        return result;
    }

    private static bool TryCreateRecoveryIdentity(
        LocalCardPaymentAttempt? attempt,
        LocalSquarePaymentAttempt? squareAttempt,
        out CardRecoveryAttemptKey key,
        out Guid orderGuid)
    {
        key = default;
        orderGuid = Guid.Empty;
        if ((attempt is null) == (squareAttempt is null))
        {
            return false;
        }

        var draftJson = attempt?.OrderDraftJson ?? squareAttempt!.OrderDraftJson;
        CardPaymentOrderDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(draftJson, CardAttemptJsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (draft is null ||
            draft.OrderGuid == Guid.Empty ||
            draft.Session is null ||
            draft.CartSnapshot?.Lines is not { Count: > 0 } ||
            draft.CurrentTenders is null ||
            draft.CreatedAt == default)
        {
            return false;
        }

        key = attempt is not null
            ? new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid)
            : new CardRecoveryAttemptKey(CardProcessorKind.Square, squareAttempt!.AttemptGuid);
        orderGuid = draft.OrderGuid;
        return true;
    }

    private async Task<bool> TryRecordCurrentSquareRefundResponseAsync(
        Guid attemptGuid,
        string submissionToken,
        string refundId,
        string refundStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var repository = squarePaymentAttemptRepository;
        var current = repository is null
            ? null
            : await repository.GetAttemptAsync(attemptGuid, cancellationToken);
        if (!IsCurrentSquareRefundWorker(current, submissionToken))
        {
            return false;
        }

        var expected = current!;
        var persistedAt = updatedAt > expected.UpdatedAt
            ? updatedAt
            : NextAttemptTimestamp(expected.UpdatedAt);
        // 先读取当前版本再 CAS，确保同 token 的迟到 worker 也不能跨越 FinalizePending 或并发终态。
        return await repository!.TryRecordRefundResponseAsync(
            attemptGuid,
            expected.Status,
            expected.UpdatedAt,
            submissionToken,
            refundId,
            refundStatus,
            persistedAt,
            cancellationToken);
    }

    private async Task<bool> TryMarkCurrentSquareRefundPaymentVerifiedAsync(
        Guid attemptGuid,
        string submissionToken,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var repository = squarePaymentAttemptRepository;
        var current = repository is null
            ? null
            : await repository.GetAttemptAsync(attemptGuid, cancellationToken);
        if (!IsCurrentSquareRefundWorker(current, submissionToken))
        {
            return false;
        }

        var expected = current!;
        var persistedAt = completedAt > expected.UpdatedAt
            ? completedAt
            : NextAttemptTimestamp(expected.UpdatedAt);
        return await repository!.TryMarkRefundPaymentVerifiedAsync(
            attemptGuid,
            expected.Status,
            expected.UpdatedAt,
            submissionToken,
            paymentId,
            paymentStatus,
            responseCode,
            responseText,
            persistedAt,
            cancellationToken);
    }

    private async Task<bool> TryMarkCurrentSquareRefundFailedAsync(
        Guid attemptGuid,
        string submissionToken,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken,
        string? cancelReason = null)
    {
        var repository = squarePaymentAttemptRepository;
        var current = repository is null
            ? null
            : await repository.GetAttemptAsync(attemptGuid, cancellationToken);
        if (!IsCurrentSquareRefundWorker(current, submissionToken))
        {
            return false;
        }

        var expected = current!;
        var persistedAt = resolvedAt > expected.UpdatedAt
            ? resolvedAt
            : NextAttemptTimestamp(expected.UpdatedAt);
        return await repository!.TryMarkRefundFailedAsync(
            attemptGuid,
            expected.Status,
            expected.UpdatedAt,
            submissionToken,
            status,
            checkoutStatus,
            paymentStatus,
            responseCode,
            responseText,
            persistedAt,
            cancellationToken,
            cancelReason);
    }

    private async Task<LocalSquarePaymentAttempt?> PersistSquareRefundFailureHandoffAsync(
        LocalSquarePaymentAttempt observedAttempt,
        string submissionToken,
        PaymentAuthorizationResult authorization,
        CancellationToken cancellationToken)
    {
        var repository = squarePaymentAttemptRepository
            ?? throw new InvalidOperationException("Square payment attempt repository is not configured.");
        var paymentStatus = observedAttempt.PaymentStatus!.Trim().ToUpperInvariant();
        var updatedAt = NextAttemptTimestamp(observedAttempt.UpdatedAt);
        try
        {
            await RunLocalStoreAsync(
                () => repository.TryPersistRefundFailureForFinalizationAsync(
                    observedAttempt.AttemptGuid,
                    observedAttempt.Status,
                    observedAttempt.UpdatedAt,
                    submissionToken,
                    paymentStatus,
                    observedAttempt.ResponseCode ?? authorization.ResponseCode,
                    observedAttempt.ResponseText ?? authorization.Message,
                    updatedAt,
                    cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 写入异常后仍重读数据库。只有仍在开放队列且证据与当前退款一致的 winner
            // 才能作为 handoff 身份返回；绝不能退回调用前的对象快照。
            LogCardRecoveryWarning(
                "persist-square-refund-failure-finalization",
                observedAttempt.AttemptGuid,
                ex);
        }

        // 无论本 worker 是 CAS 赢家还是输家，都必须重读并服从数据库中的金融证据。
        var winner = await ReadSquareRefundFailureHandoffWinnerAsync(
            repository,
            observedAttempt.AttemptGuid,
            cancellationToken);
        if (winner is null)
        {
            LogCardRecoveryWarning(
                "verify-square-refund-failure-finalization",
                observedAttempt.AttemptGuid,
                new InvalidOperationException("Square refund attempt disappeared after finalization handoff."));
            return null;
        }

        if (IsQueueReachableSquareRefundFailureHandoff(winner, submissionToken) ||
            IsQueueReachableStrongerSquareRefundEvidence(winner))
        {
            return winner;
        }

        // 兼容写入异常、旧版本或并发通用失败写入：同一退款 worker 已保存 FAILED/REJECTED
        // 但尚未建立 FinalizePending 时，必须先精确补齐耐久 handoff；其它终态保持单调。
        if (IsRepairableSquareRefundFailure(winner, submissionToken))
        {
            var repairUpdatedAt = NextAttemptTimestamp(winner.UpdatedAt);
            try
            {
                await RunLocalStoreAsync(
                    () => repository.TryPersistRefundFailureForFinalizationAsync(
                        winner.AttemptGuid,
                        winner.Status,
                        winner.UpdatedAt,
                        submissionToken,
                        winner.PaymentStatus!.Trim().ToUpperInvariant(),
                        winner.ResponseCode ?? authorization.ResponseCode,
                        winner.ResponseText ?? authorization.Message,
                        repairUpdatedAt,
                        cancellationToken),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LogCardRecoveryWarning(
                    "repair-square-refund-failure-finalization",
                    winner.AttemptGuid,
                    ex);
            }

            winner = await ReadSquareRefundFailureHandoffWinnerAsync(
                repository,
                observedAttempt.AttemptGuid,
                cancellationToken);
            if (winner is not null &&
                (IsQueueReachableSquareRefundFailureHandoff(winner, submissionToken) ||
                 IsQueueReachableStrongerSquareRefundEvidence(winner)))
            {
                return winner;
            }
        }

        LogCardRecoveryWarning(
            "verify-square-refund-failure-finalization",
            observedAttempt.AttemptGuid,
            new InvalidOperationException(
                "Square refund finalization CAS was lost to an incompatible state; no recovery identity was returned."));
        return null;
    }

    private async Task<LocalSquarePaymentAttempt?> ReadSquareRefundFailureHandoffWinnerAsync(
        ILocalSquarePaymentAttemptRepository repository,
        Guid attemptGuid,
        CancellationToken cancellationToken) =>
        await RunLocalStoreAsync(
            () => repository.GetAttemptAsync(attemptGuid, cancellationToken),
            cancellationToken);

    private static bool IsQueueReachableSquareRefundFailureHandoff(
        LocalSquarePaymentAttempt attempt,
        string submissionToken) =>
        IsQueueReachableSquareRefundAttempt(attempt) &&
        string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) &&
        IsTerminalSquareRefundFailure(attempt.PaymentStatus) &&
        attempt.Status == LocalSquarePaymentAttemptStatus.Unknown &&
        string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) &&
        attempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.Abandoned;

    private static bool IsQueueReachableStrongerSquareRefundEvidence(LocalSquarePaymentAttempt attempt) =>
        IsQueueReachableSquareRefundAttempt(attempt) &&
        HasStrongerSquareRefundEvidence(attempt);

    private static bool IsQueueReachableSquareRefundAttempt(LocalSquarePaymentAttempt attempt) =>
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        attempt.Status is not (
            LocalSquarePaymentAttemptStatus.Canceled or
            LocalSquarePaymentAttemptStatus.TimedOut or
            LocalSquarePaymentAttemptStatus.Failed or
            LocalSquarePaymentAttemptStatus.OrderCompleted or
            LocalSquarePaymentAttemptStatus.Abandoned);

    private static bool IsRepairableSquareRefundFailure(
        LocalSquarePaymentAttempt attempt,
        string submissionToken) =>
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) &&
        (attempt.Status is LocalSquarePaymentAttemptStatus.Pending or
            LocalSquarePaymentAttemptStatus.Recovering or
            LocalSquarePaymentAttemptStatus.Unknown or
            LocalSquarePaymentAttemptStatus.Failed) &&
        IsTerminalSquareRefundFailure(attempt.PaymentStatus) &&
        string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) &&
        attempt.RecoveryTargetStatus is null &&
        !IsSquareSupervisorResolution(attempt.ResponseCode);

    private static bool IsSquareSupervisorResolution(string? responseCode) =>
        string.Equals(responseCode, ActiveSessionSupervisorResolutionCodes.ConfirmedPaid, StringComparison.Ordinal) ||
        string.Equals(responseCode, ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid, StringComparison.Ordinal) ||
        string.Equals(responseCode, CardRefundSupervisorResolutionCodes.ConfirmedRefunded, StringComparison.Ordinal) ||
        string.Equals(responseCode, CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded, StringComparison.Ordinal);

    private static bool IsTerminalSquareRefundFailure(string? paymentStatus) =>
        string.Equals(paymentStatus?.Trim(), "FAILED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(paymentStatus?.Trim(), "REJECTED", StringComparison.OrdinalIgnoreCase);

    private static bool HasStrongerSquareRefundEvidence(LocalSquarePaymentAttempt attempt) =>
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(attempt.PaymentStatus?.Trim(), "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
         attempt.Status is LocalSquarePaymentAttemptStatus.PaymentVerified or LocalSquarePaymentAttemptStatus.OrderCompleted ||
         string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) &&
         attempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted ||
         string.Equals(
             attempt.ResponseCode,
             CardRefundSupervisorResolutionCodes.ConfirmedRefunded,
             StringComparison.Ordinal) &&
         !string.IsNullOrWhiteSpace(attempt.SupervisorFinancialReference));

    private static bool IsCurrentSquareRefundWorker(
        LocalSquarePaymentAttempt? attempt,
        string submissionToken) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal);

    private async Task MarkSquareAttemptRequiresRecoveryAsync(
        LocalSquarePaymentAttempt? squareAttempt,
        PaymentAuthorizationResult authorization,
        string message,
        string? refundSubmissionToken = null)
    {
        if (squareAttempt is null)
        {
            return;
        }

        await RunLocalStoreAsync(
            () => refundSubmissionToken is not null
                ? RequireRefundCasAsync(
                    TryMarkCurrentSquareRefundFailedAsync(
                        squareAttempt.AttemptGuid,
                        refundSubmissionToken,
                        LocalSquarePaymentAttemptStatus.Unknown,
                        null,
                        authorization.ResponseText,
                        authorization.ResponseCode,
                        message,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None))
                : squarePaymentAttemptRepository!.MarkFailedAsync(
                    squareAttempt.AttemptGuid,
                    LocalSquarePaymentAttemptStatus.Unknown,
                    null,
                    authorization.ResponseText,
                    authorization.ResponseCode,
                    message,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None),
            CancellationToken.None);
    }

    private static void LogCardRecoveryWarning(string stage, Guid? attemptGuid, Exception ex)
    {
        TryWriteCardRecoveryLog(
            $"conservative result-unknown stage={stage} attemptGuid={attemptGuid?.ToString("D") ?? "<none>"} error={ex.GetType().Name}");
    }

    private static void TryWriteCardRecoveryLog(string message)
    {
        try
        {
            // 金融结果和专用异常契约已经确定后，诊断通道只能尽力写入，不能反向改变业务结果。
            ConsoleLog.Write("CardRecovery", message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 普通控制台、Trace、文件或测试订阅者异常不得替代支付恢复结果。
        }
    }

    private async Task<(
        LocalCardPaymentAttempt? Attempt,
        bool Reused,
        bool RequiresRecovery)> TryCreateLinklyPaymentAttemptAsync(
        CardTerminalSettings settings,
        decimal amount,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        PosCartSnapshot? cartSnapshot,
        string? referenceText,
        bool isRefund,
        CancellationToken cancellationToken)
    {
        if (cardPaymentAttemptRepository is null || cartSnapshot is null)
        {
            return (null, false, false);
        }

        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
        if (settings.Processor != CardProcessorKind.Linkly ||
            (!isRefund && mode != LinklyConnectionMode.CloudBackendAsync &&
             mode != LinklyConnectionMode.LocalIp && mode != LinklyConnectionMode.CloudDirectSync))
        {
            return (null, false, false);
        }

        if (isRefund)
        {
            var openRefundAttempts = await cardPaymentAttemptRepository.GetOpenRefundAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                settings.Environment.ToString(),
                cancellationToken);
            var existingAttempt = openRefundAttempts.FirstOrDefault(candidate =>
                candidate.Amount == amount &&
                RefundDraftMatches(candidate.OrderDraftJson, referenceText));
            if (existingAttempt is not null)
            {
                return (
                    existingAttempt,
                    true,
                    RequiresLinklyRefundRecoveryForCurrentMode(existingAttempt, mode));
            }
        }

        var now = DateTimeOffset.UtcNow;
        var refundBusinessKey = isRefund
            ? BuildRefundBusinessKey(
                CardProcessorKind.Linkly,
                settings.Environment.ToString(),
                session,
                amount,
                referenceText,
                "AUD")
            : null;
        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            session,
            cartSnapshot,
            currentTenders.ToArray(),
            actualAmount,
            amount,
            isRefund ? "R" : "P",
            referenceText,
            now);
        var attemptGuid = Guid.NewGuid();
        var attempt = new LocalCardPaymentAttempt(
            attemptGuid,
            null,
            // LocalIp 与 CloudBackendAsync 的退款引用都只绑定已落库 attempt 身份：后端模式把 attempt 身份作为 AttemptGuid
            // 交给后端，后端用同一算法派生出相同的 R 引用。过去后端退款用本地随机 GUID，POST 已受理而响应丢失时，
            // 订单会话关联不上本地退款 attempt，出现两条互不关联的记录，主管误判“未退款”重试会重复退款。
            // 直连模式的退款引用仍沿用既有规则。
            // 销售在三种模式下都必须在发请求前确定引用并随 attempt 落库：CloudBackendAsync 过去等服务端生成，
            // 请求发出后一旦断电或响应丢失，这一行 SessionId 与 TxnRef 皆空，自动恢复和主管结案都无法认领它。
            isRefund
                ? mode is LinklyConnectionMode.LocalIp or LinklyConnectionMode.CloudBackendAsync
                    ? LinklyLocalTxnRef.Create('R', attemptGuid.ToString("D"))
                    : BuildRefundTxnRef(referenceText)
                : LinklyLocalTxnRef.Create('P', attemptGuid.ToString("D")),
            settings.Processor.ToString(),
            settings.Environment.ToString(),
            CardTerminalSettings.FormatLinklyConnectionMode(mode),
            isRefund ? "R" : "P",
            amount,
            LocalCardPaymentAttemptStatus.Pending,
            JsonSerializer.Serialize(draft, CardAttemptJsonOptions),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            null,
            null,
            null,
            now,
            now,
            null,
            null,
            isRefund ? "Refund" : "Sale",
            isRefund ? draft.OrderGuid : null,
            SubmissionToken: null,
            RefundBusinessKey: refundBusinessKey);

        var persistedAttempt = isRefund
            ? await cardPaymentAttemptRepository.CreateOrGetOpenRefundAsync(attempt, cancellationToken)
            : attempt;
        if (!isRefund)
        {
            await cardPaymentAttemptRepository.CreateAsync(attempt, cancellationToken);
        }

        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "payment-attempt",
            "created",
            environment: settings.Environment,
            details: new
            {
                timestamp = DateTimeOffset.Now,
                attemptGuid = persistedAttempt.AttemptGuid,
                localStatus = persistedAttempt.Status.ToString(),
                txnType = persistedAttempt.TxnType,
                amount = persistedAttempt.Amount,
                processor = persistedAttempt.Processor,
                connectionMode = persistedAttempt.ConnectionMode,
                storeCode = persistedAttempt.StoreCode,
                deviceCode = persistedAttempt.DeviceCode,
                cashierId = persistedAttempt.CashierId,
                createdAt = persistedAttempt.CreatedAt,
                updatedAt = persistedAttempt.UpdatedAt
            });
        return (
            persistedAttempt,
            persistedAttempt.AttemptGuid != attempt.AttemptGuid,
            isRefund && RequiresLinklyRefundRecoveryForCurrentMode(persistedAttempt, mode));
    }

    private static bool IsCloudBackendAsyncAttempt(LocalCardPaymentAttempt? attempt)
    {
        return string.Equals(
            attempt?.ConnectionMode?.Trim(),
            nameof(LinklyConnectionMode.CloudBackendAsync),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresLinklyRefundRecoveryForCurrentMode(
        LocalCardPaymentAttempt attempt,
        LinklyConnectionMode mode)
    {
        var expectedMode = CardTerminalSettings.FormatLinklyConnectionMode(mode);
        if (!string.Equals(attempt.ConnectionMode?.Trim(), expectedMode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return mode == LinklyConnectionMode.LocalIp &&
            (!string.Equals(attempt.TxnType, "R", StringComparison.Ordinal) ||
             !LinklyLocalTxnRef.TryNormalizeHistoricalReference(attempt.TxnRef, out _));
    }

    private async Task<(LocalSquarePaymentAttempt? Attempt, bool Reused)> TryCreateSquarePaymentAttemptAsync(
        CardTerminalSettings settings,
        decimal amount,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> currentTenders,
        PosCartSnapshot? cartSnapshot,
        string? referenceText,
        bool isRefund,
        CancellationToken cancellationToken)
    {
        if (squarePaymentAttemptRepository is null ||
            cartSnapshot is null)
        {
            return (null, false);
        }

        if (settings.Processor != CardProcessorKind.Square ||
            string.IsNullOrWhiteSpace(settings.SquareDeviceId) ||
            string.IsNullOrWhiteSpace(settings.SquareLocationId))
        {
            return (null, false);
        }

        if (isRefund)
        {
            var openRefundAttempts = await squarePaymentAttemptRepository.GetOpenRefundAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                settings.Environment.ToString(),
                cancellationToken);
            var existingAttempt = openRefundAttempts.FirstOrDefault(candidate =>
                candidate.Amount == amount &&
                RefundDraftMatches(candidate.OrderDraftJson, referenceText));
            if (existingAttempt is not null)
            {
                return (existingAttempt, true);
            }
        }

        const string currency = "AUD";
        var now = DateTimeOffset.UtcNow;
        var refundBusinessKey = isRefund
            ? BuildRefundBusinessKey(
                CardProcessorKind.Square,
                settings.Environment.ToString(),
                session,
                amount,
                referenceText,
                currency)
            : null;
        var saleSubmissionToken = isRefund ? null : Guid.NewGuid().ToString("N");
        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            session,
            cartSnapshot,
            currentTenders.ToArray(),
            actualAmount,
            amount,
            isRefund ? "R" : "P",
            referenceText,
            now);
        var attempt = new LocalSquarePaymentAttempt(
            Guid.NewGuid(),
            null,
            Guid.NewGuid().ToString("N"),
            SquareDeviceIdNormalizer.NormalizeForTerminalCheckout(settings.SquareDeviceId) ?? settings.SquareDeviceId,
            settings.SquareLocationId,
            settings.Environment.ToString(),
            amount,
            ToMinorUnits(amount),
            currency,
            LocalSquarePaymentAttemptStatus.Pending,
            null,
            null,
            JsonSerializer.Serialize(draft, CardAttemptJsonOptions),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            null,
            null,
            null,
            null,
            now,
            now,
            null,
            null,
            null,
            isRefund ? "Refund" : "Sale",
            // 销售同样固化订单身份；恢复时不能只信任可被损坏的 draft JSON。
            draft.OrderGuid,
            SubmissionToken: saleSubmissionToken,
            RefundBusinessKey: refundBusinessKey);

        var persistedAttempt = isRefund
            ? await squarePaymentAttemptRepository.CreateOrGetOpenRefundAsync(attempt, cancellationToken)
            : attempt;
        if (!isRefund)
        {
            await squarePaymentAttemptRepository.CreateAsync(attempt, cancellationToken);
        }

        return (persistedAttempt, persistedAttempt.AttemptGuid != attempt.AttemptGuid);
    }

    private static bool RefundDraftMatches(string orderDraftJson, string? originalReference)
    {
        try
        {
            var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(orderDraftJson, CardAttemptJsonOptions);
            return string.Equals(
                draft?.OriginalReference?.Trim(),
                originalReference?.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // 同终端、同金额的未决退款草稿损坏时无法证明是另一笔交易，必须保守阻止重复退款。
            return true;
        }
    }

    private static string BuildRefundTxnRef(string? originalReference)
    {
        var normalizedOriginalReference = originalReference?.Trim();
        if (normalizedOriginalReference?.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase) == true)
        {
            normalizedOriginalReference = normalizedOriginalReference[4..].Trim();
        }

        string txnRef;
        do
        {
            txnRef = Guid.NewGuid().ToString("N");
        }
        while (string.Equals(txnRef, normalizedOriginalReference, StringComparison.OrdinalIgnoreCase));

        return txnRef;
    }

    private static string BuildRefundBusinessKey(
        CardProcessorKind processor,
        string environment,
        PosSessionState session,
        decimal amount,
        string? originalReference,
        string currency)
    {
        var canonical = string.Join(
            "\n",
            "ordinary-card-refund-v1",
            processor.ToString().ToUpperInvariant(),
            environment.Trim().ToUpperInvariant(),
            session.StoreCode.Trim().ToUpperInvariant(),
            session.DeviceCode.Trim().ToUpperInvariant(),
            originalReference?.Trim().ToUpperInvariant() ?? string.Empty,
            ToMinorUnits(amount).ToString(CultureInfo.InvariantCulture),
            currency.Trim().ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static DateTimeOffset NextAttemptTimestamp(DateTimeOffset current)
    {
        var now = DateTimeOffset.UtcNow;
        return now > current ? now : current.AddTicks(1);
    }

    private static async Task RequireRefundCasAsync(Task<bool> update)
    {
        if (!await update)
        {
            throw new InvalidOperationException("退款 attempt 已被其他任务推进或主管结案。");
        }
    }

    private async Task MarkCurrentCardAttemptRecoveringWithCasAsync(
        Guid attemptGuid,
        CancellationToken cancellationToken)
    {
        var repository = cardPaymentAttemptRepository ??
            throw new InvalidOperationException("Linkly attempt repository is unavailable.");
        var current = await repository.GetAttemptAsync(attemptGuid, cancellationToken);
        if (current is null ||
            !await repository.TryMarkRecoveringAsync(
                current.AttemptGuid,
                current.Status,
                current.UpdatedAt,
                NextAttemptTimestamp(current.UpdatedAt),
                cancellationToken))
        {
            throw new InvalidOperationException("付款 attempt 已被其他任务推进、终态化或主管结案。");
        }
    }

    private async Task UpdateCardPaymentAttemptAfterAuthorizationAsync(
        Guid attemptGuid,
        PaymentAuthorizationResult authorization,
        CancellationToken cancellationToken,
        LocalCardPaymentAttemptStatus? statusOverride = null,
        string? responseTextOverride = null,
        string? refundSubmissionToken = null)
    {
        // 终端已经返回后，attempt 结果是金融恢复边界，不能受 UI 取消影响。
        var persistenceCancellationToken = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(authorization.SessionId))
        {
            await RunLocalStoreAsync(
                () => refundSubmissionToken is not null
                    ? RequireRefundCasAsync(
                        cardPaymentAttemptRepository!.TryUpdateRefundSessionAsync(
                            attemptGuid,
                            refundSubmissionToken,
                            authorization.SessionId,
                            authorization.TxnRef,
                            now,
                            persistenceCancellationToken))
                    : cardPaymentAttemptRepository!.UpdateSessionAsync(
                        attemptGuid,
                        authorization.SessionId,
                        authorization.TxnRef,
                        now,
                        persistenceCancellationToken),
                persistenceCancellationToken);
        }

        if (authorization.ResultUnknown)
        {
            // 已提交到终端但结果未知时必须保留为可恢复状态，避免被当作普通超时失败后允许重新刷卡。
            await RunLocalStoreAsync(
                () => refundSubmissionToken is not null
                    ? RequireRefundCasAsync(
                        cardPaymentAttemptRepository!.TryMarkRefundRecoveringAsync(
                            attemptGuid,
                            refundSubmissionToken,
                            now,
                            persistenceCancellationToken))
                    : MarkCurrentCardAttemptRecoveringWithCasAsync(
                        attemptGuid,
                        persistenceCancellationToken),
                persistenceCancellationToken);
            return;
        }

        var firstTransaction = authorization.CardTransactions?.FirstOrDefault();
        var status = statusOverride ?? (authorization.Approved
            ? LocalCardPaymentAttemptStatus.Approved
            : MapCardAttemptFailureStatus(
                authorization.Message,
                firstTransaction?.ResponseText,
                firstTransaction?.ResponseCode ?? authorization.ResponseCode));
        await RunLocalStoreAsync(
            () => refundSubmissionToken is not null
                ? RequireRefundCasAsync(
                    cardPaymentAttemptRepository!.TryUpdateRefundOutcomeAsync(
                        attemptGuid,
                        refundSubmissionToken,
                        status,
                        firstTransaction?.ResponseCode ?? authorization.ResponseCode,
                        responseTextOverride ?? firstTransaction?.ResponseText ?? authorization.ResponseText ?? authorization.Message,
                        authorization.Reference,
                        now,
                        persistenceCancellationToken))
                : cardPaymentAttemptRepository!.UpdateOutcomeAsync(
                    attemptGuid,
                    status,
                    firstTransaction?.ResponseCode ?? authorization.ResponseCode,
                    responseTextOverride ?? firstTransaction?.ResponseText ?? authorization.ResponseText ?? authorization.Message,
                    authorization.Reference,
                    now,
                    persistenceCancellationToken),
            persistenceCancellationToken);
    }

    private async Task<bool> TryPersistCardPaymentAttemptAfterFinancialResultAsync(
        Guid attemptGuid,
        PaymentAuthorizationResult authorization,
        CancellationToken cancellationToken,
        LocalCardPaymentAttemptStatus? statusOverride = null,
        string? responseTextOverride = null,
        string? refundSubmissionToken = null)
    {
        try
        {
            await UpdateCardPaymentAttemptAfterAuthorizationAsync(
                attemptGuid,
                authorization,
                cancellationToken,
                statusOverride,
                responseTextOverride,
                refundSubmissionToken);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogCardRecoveryWarning("persist-financial-result", attemptGuid, ex);
            try
            {
                await RunLocalStoreAsync(
                    () => refundSubmissionToken is not null
                        ? RequireRefundCasAsync(
                            cardPaymentAttemptRepository!.TryMarkRefundRecoveringAsync(
                                attemptGuid,
                                refundSubmissionToken,
                                DateTimeOffset.UtcNow,
                                CancellationToken.None))
                        : MarkCurrentCardAttemptRecoveringWithCasAsync(
                            attemptGuid,
                            CancellationToken.None),
                    CancellationToken.None);
            }
            catch (Exception recoveryException) when (recoveryException is not OutOfMemoryException and not StackOverflowException)
            {
                LogCardRecoveryWarning("persist-financial-result-recovery", attemptGuid, recoveryException);
            }

            return false;
        }
    }

    private static LocalCardPaymentAttemptStatus MapCardAttemptFailureStatus(
        string? message,
        string? responseText,
        string? responseCode)
    {
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

        var text = $"{message} {responseText}".ToUpperInvariant();
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

    private static LinklyConnectionMode ResolveAttemptConnectionMode(
        LocalCardPaymentAttempt attempt,
        LinklyConnectionMode fallback)
    {
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(attempt.ConnectionMode, fallback);
        // LocalIp 没有 backend session；若 attempt 已绑定 SessionId，说明实际已 fallback 到后端异步链路。
        return mode == LinklyConnectionMode.LocalIp && !string.IsNullOrWhiteSpace(attempt.SessionId)
            ? LinklyConnectionMode.CloudBackendAsync
            : mode;
    }

    private static LocalSquarePaymentAttemptStatus MapSquareAuthorizationFailureStatus(
        string? statusKey,
        string? message)
    {
        // Square 友好状态键比英文 message 更稳定，优先用它保留本地 attempt 的真实分类。
        switch (statusKey)
        {
            case "payment.card.squareTimedOut":
            case "payment.card.squareTerminalNotPickedUp":
                return LocalSquarePaymentAttemptStatus.TimedOut;
            case "payment.card.squareCanceled":
            case "payment.card.squareCanceledBuyer":
            case "payment.card.squareCanceledSeller":
                return LocalSquarePaymentAttemptStatus.Canceled;
            case "payment.card.squareTerminalOffline":
                return LocalSquarePaymentAttemptStatus.Failed;
        }

        var text = (message ?? string.Empty).ToUpperInvariant();
        if (text.Contains("TIMEOUT", StringComparison.Ordinal) ||
            text.Contains("TIMED OUT", StringComparison.Ordinal))
        {
            return LocalSquarePaymentAttemptStatus.TimedOut;
        }

        if (text.Contains("CANCEL", StringComparison.Ordinal))
        {
            return LocalSquarePaymentAttemptStatus.Canceled;
        }

        if (text.Contains("UNKNOWN", StringComparison.Ordinal) ||
            text.Contains("CONFIRM", StringComparison.Ordinal))
        {
            return LocalSquarePaymentAttemptStatus.Unknown;
        }

        return LocalSquarePaymentAttemptStatus.Failed;
    }

    private static long ToMinorUnits(decimal amount)
    {
        return decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private static string FormatCardAttemptTenderKey(Guid attemptGuid)
    {
        return $"CARD_ATTEMPT:{attemptGuid:N}";
    }

    private static string FormatSquareAttemptTenderKey(Guid attemptGuid)
    {
        return $"SQUARE_ATTEMPT:{attemptGuid:N}";
    }

    private static bool TryReadCardAttemptTenderKey(string? value, out Guid attemptGuid)
    {
        attemptGuid = Guid.Empty;
        const string prefix = "CARD_ATTEMPT:";
        return !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(value[prefix.Length..], "N", out attemptGuid);
    }

    private static bool TryReadSquareAttemptTenderKey(string? value, out Guid attemptGuid)
    {
        attemptGuid = Guid.Empty;
        const string prefix = "SQUARE_ATTEMPT:";
        return !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(value[prefix.Length..], "N", out attemptGuid);
    }

    private static void RollbackRecoveryPublications(
        PosCartService cart,
        IReadOnlyList<PaymentTender> tenders,
        Guid? recoveryOwnerAttemptGuid,
        CardRecoveryAttemptKey? recoveryOwnerAttemptKey)
    {
        if (recoveryOwnerAttemptKey is CardRecoveryAttemptKey ownerKey)
        {
            var rollback = cart.RollbackRecoveryPublication(ownerKey);
            if (rollback.NotificationWarning)
            {
                TryWriteCardRecoveryLog(
                    $"recovery cart rollback notification warning provider={ownerKey.Processor} attemptGuid={ownerKey.AttemptGuid}");
            }

            return;
        }

        // 旧 GUID publication 仅保留兼容路径；provider-aware publication 永远不会进入该分支。
        var attemptGuids = new HashSet<Guid>();
        if (recoveryOwnerAttemptGuid is Guid ownerAttemptGuid)
        {
            attemptGuids.Add(ownerAttemptGuid);
        }

        foreach (var tender in tenders.Where(tender => tender.Method == PaymentMethodKind.Card))
        {
            if (TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var cardAttemptGuid))
            {
                attemptGuids.Add(cardAttemptGuid);
            }

            if (TryReadSquareAttemptTenderKey(tender.IdempotencyKey, out var squareAttemptGuid))
            {
                attemptGuids.Add(squareAttemptGuid);
            }
        }

        foreach (var attemptGuid in attemptGuids)
        {
            var rollback = cart.RollbackRecoveryPublication(attemptGuid);
            if (rollback.NotificationWarning)
            {
                TryWriteCardRecoveryLog(
                    $"recovery cart rollback notification warning attemptGuid={attemptGuid}");
            }
        }
    }

    private static bool IsCapturedRecoveryOwner(
        CardRecoveryAttemptKey? recoveryOwnerAttemptKey,
        Guid? recoveryOwnerAttemptGuid,
        CardRecoveryAttemptKey expectedKey) =>
        recoveryOwnerAttemptKey is CardRecoveryAttemptKey capturedKey
            ? capturedKey == expectedKey
            : recoveryOwnerAttemptGuid == expectedKey.AttemptGuid;

    private async Task MarkApprovedCardPersistenceRequiresReviewAsync(
        IReadOnlyList<PaymentTender> tenders,
        Exception persistenceException)
    {
        const string reviewMessage =
            "The card was approved, but the local order could not be safely persisted.";
        var now = DateTimeOffset.UtcNow;

        if (cardPaymentAttemptRepository is not null)
        {
            foreach (var tender in tenders.Where(tender => tender.Method == PaymentMethodKind.Card))
            {
                if (!TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid))
                {
                    continue;
                }

                try
                {
                    var attempt = await RunLocalStoreAsync(
                        () => cardPaymentAttemptRepository.GetAttemptAsync(
                            attemptGuid,
                            CancellationToken.None),
                        CancellationToken.None);
                    if (attempt is null ||
                        string.Equals(
                            attempt.RecoveryPhase,
                            CardRecoveryPhases.FinalizePending,
                            StringComparison.Ordinal) ||
                        IsTerminalCardAttemptStatus(attempt.Status) ||
                        IsSupervisorResolutionCode(attempt.ResponseCode))
                    {
                        continue;
                    }

                    var responseText = string.IsNullOrWhiteSpace(attempt.ResponseText)
                        ? reviewMessage
                        : attempt.ResponseText;
                    var paymentReference = string.IsNullOrWhiteSpace(attempt.PaymentReference)
                        ? tender.Reference
                        : attempt.PaymentReference;
                    // 已批准付款已有确定金融结果；订单保存失败后直接进入可重放最终化，禁止降级为无出口的 RequiresReview。
                    var updated = attempt.Status == LocalCardPaymentAttemptStatus.Approved
                        ? await RunLocalStoreAsync(
                            () => cardPaymentAttemptRepository.TryPersistRecoveryOutcomeAsync(
                                attemptGuid,
                                LocalCardPaymentAttemptStatus.Approved,
                                attempt.ResponseCode,
                                responseText,
                                paymentReference,
                                attempt.Status,
                                attempt.UpdatedAt,
                                LocalCardPaymentAttemptStatus.OrderCompleted,
                                now,
                                CancellationToken.None),
                            CancellationToken.None)
                        : await RunLocalStoreAsync(
                            () => cardPaymentAttemptRepository.TryUpdateOutcomeAsync(
                                attemptGuid,
                                attempt.Status,
                                attempt.UpdatedAt,
                                LocalCardPaymentAttemptStatus.RequiresReview,
                                attempt.ResponseCode,
                                responseText,
                                paymentReference,
                                now,
                                CancellationToken.None),
                            CancellationToken.None);
                    if (!updated)
                    {
                        var winner = await RunLocalStoreAsync(
                            () => cardPaymentAttemptRepository.GetAttemptAsync(
                                attemptGuid,
                                CancellationToken.None),
                            CancellationToken.None);
                        TryWriteCardRecoveryLog(
                            $"mark persistence recovery CAS lost attemptGuid={attemptGuid} winnerStatus={winner?.Status} winnerPhase={winner?.RecoveryPhase}");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LogCardRecoveryWarning("mark-approved-order-persistence-review", attemptGuid, ex);
                }
            }
        }

        if (squarePaymentAttemptRepository is not null)
        {
            foreach (var tender in tenders.Where(tender => tender.Method == PaymentMethodKind.Card))
            {
                if (!TryReadSquareAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid))
                {
                    continue;
                }

                try
                {
                    var attempt = await RunLocalStoreAsync(
                        () => squarePaymentAttemptRepository.GetAttemptAsync(
                            attemptGuid,
                            CancellationToken.None),
                        CancellationToken.None);
                    if (attempt is null ||
                        string.Equals(
                            attempt.RecoveryPhase,
                            CardRecoveryPhases.FinalizePending,
                            StringComparison.Ordinal) ||
                        IsTerminalSquareAttemptStatus(attempt.Status) ||
                        IsSupervisorResolutionCode(attempt.ResponseCode))
                    {
                        continue;
                    }

                    var responseText = string.IsNullOrWhiteSpace(attempt.ResponseText)
                        ? reviewMessage
                        : attempt.ResponseText;
                    var paymentVerified =
                        attempt.Status == LocalSquarePaymentAttemptStatus.PaymentVerified &&
                        !string.IsNullOrWhiteSpace(attempt.PaymentId) &&
                        string.Equals(attempt.PaymentStatus?.Trim(), "COMPLETED", StringComparison.OrdinalIgnoreCase);
                    // Square 完成证据必须保持 PaymentVerified，并在无 CheckoutId 时仍可从本地两阶段恢复。
                    var updated = paymentVerified
                        ? await RunLocalStoreAsync(
                            () => squarePaymentAttemptRepository.TryBeginRecoveryFinalizationAsync(
                                attemptGuid,
                                attempt.Status,
                                attempt.UpdatedAt,
                                LocalSquarePaymentAttemptStatus.OrderCompleted,
                                now,
                                CancellationToken.None),
                            CancellationToken.None)
                        : await RunLocalStoreAsync(
                            () => squarePaymentAttemptRepository.TryMarkFailedAsync(
                                attemptGuid,
                                attempt.Status,
                                attempt.UpdatedAt,
                                LocalSquarePaymentAttemptStatus.Unknown,
                                attempt.CheckoutStatus,
                                attempt.PaymentStatus,
                                attempt.ResponseCode,
                                responseText,
                                now,
                                CancellationToken.None,
                                attempt.CancelReason),
                            CancellationToken.None);
                    if (!updated)
                    {
                        var winner = await RunLocalStoreAsync(
                            () => squarePaymentAttemptRepository.GetAttemptAsync(
                                attemptGuid,
                                CancellationToken.None),
                            CancellationToken.None);
                        TryWriteCardRecoveryLog(
                            $"mark Square persistence recovery CAS lost attemptGuid={attemptGuid} winnerStatus={winner?.Status} winnerPhase={winner?.RecoveryPhase}");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LogCardRecoveryWarning("mark-approved-square-order-persistence-review", attemptGuid, ex);
                }
            }
        }

        LogCardRecoveryWarning("approved-order-persistence", attemptGuid: null, persistenceException);
    }

    private static bool IsTerminalCardAttemptStatus(LocalCardPaymentAttemptStatus status) => status is
        LocalCardPaymentAttemptStatus.Declined or
        LocalCardPaymentAttemptStatus.TimedOut or
        LocalCardPaymentAttemptStatus.Cancelled or
        LocalCardPaymentAttemptStatus.Failed or
        LocalCardPaymentAttemptStatus.OrderCompleted or
        LocalCardPaymentAttemptStatus.Abandoned;

    private static bool IsTerminalSquareAttemptStatus(LocalSquarePaymentAttemptStatus status) => status is
        LocalSquarePaymentAttemptStatus.Canceled or
        LocalSquarePaymentAttemptStatus.TimedOut or
        LocalSquarePaymentAttemptStatus.Failed or
        LocalSquarePaymentAttemptStatus.OrderCompleted or
        LocalSquarePaymentAttemptStatus.Abandoned;

    private static bool IsSupervisorResolutionCode(string? responseCode) =>
        !string.IsNullOrWhiteSpace(responseCode) &&
        responseCode.Trim().StartsWith("SUPERVISOR_", StringComparison.Ordinal);

    private async Task MarkCompletedCardAttemptsAsync(
        PosCartService cart,
        IReadOnlyList<PaymentTender> tenders,
        Guid? recoveryOwnerAttemptGuid,
        CardRecoveryAttemptKey? recoveryOwnerAttemptKey,
        CancellationToken cancellationToken)
    {
        if (cardPaymentAttemptRepository is null)
        {
            return;
        }

        var attemptGuids = tenders
            .Where(tender => tender.Method == PaymentMethodKind.Card)
            .Select(tender => TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : (Guid?)null)
            .Where(attemptGuid => attemptGuid is not null)
            .Select(attemptGuid => attemptGuid!.Value)
            .Distinct()
            // 中文注释：同一订单可能包含多个 Linkly tender；精确 owner 必须最后处理且不能被前一个普通告警跳过。
            .OrderBy(attemptGuid => IsCapturedRecoveryOwner(
                recoveryOwnerAttemptKey,
                recoveryOwnerAttemptGuid,
                new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid)))
            .ToArray();
        List<Exception>? nonOwnerFailures = null;
        foreach (var attemptGuid in attemptGuids)
        {
            var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid);
            var isCapturedOwner = IsCapturedRecoveryOwner(
                recoveryOwnerAttemptKey,
                recoveryOwnerAttemptGuid,
                attemptKey);
            try
            {
                var completedAt = DateTimeOffset.UtcNow;
                var attempt = await RunLocalStoreAsync(
                    () => cardPaymentAttemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                    CancellationToken.None);
                if (attempt is not null &&
                    string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal))
                {
                    if (!Enum.TryParse<LocalCardPaymentAttemptStatus>(
                            attempt.RecoveryTargetStatus,
                            ignoreCase: false,
                            out var targetStatus) ||
                        targetStatus != LocalCardPaymentAttemptStatus.OrderCompleted)
                    {
                        throw new InvalidOperationException(
                            $"Linkly attempt {attemptGuid:D} has an invalid post-order recovery target.");
                    }

                    // 中文注释：恢复草稿对应订单已落盘，此处才用原状态版本完成最终 CAS。
                    var finalized = await RunLocalStoreAsync(
                        () => cardPaymentAttemptRepository.TryFinalizeRecoveryOutcomeAsync(
                            attemptGuid,
                            attempt.Status,
                            attempt.UpdatedAt,
                            targetStatus,
                            completedAt,
                            CancellationToken.None),
                        CancellationToken.None);
                    if (!finalized)
                    {
                        var winner = await RunLocalStoreAsync(
                            () => cardPaymentAttemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                            CancellationToken.None);
                        if (winner is null ||
                            winner.Status != LocalCardPaymentAttemptStatus.OrderCompleted ||
                            !string.Equals(winner.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Linkly attempt {attemptGuid:D} order was saved but recovery finalization is still pending.");
                        }
                    }
                }
                else
                {
                    // 订单落本地后才把普通刷卡 attempt 标为完成，避免“刷卡成功但订单未写入”被误判为已恢复。
                    await RunLocalStoreAsync(
                        () => cardPaymentAttemptRepository.MarkOrderCompletedAsync(
                            attemptGuid,
                            completedAt,
                            CancellationToken.None),
                        CancellationToken.None);
                }

                if (isCapturedOwner)
                {
                    // 中文注释：只有终态持久化成功后才释放恢复购物车；失败路径由 catch 精确回滚本 attempt。
                    var completedAttempt = await RunLocalStoreAsync(
                        () => cardPaymentAttemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                        CancellationToken.None);
                    if (completedAttempt is null ||
                        completedAttempt.Status != LocalCardPaymentAttemptStatus.OrderCompleted ||
                        !string.Equals(completedAttempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) ||
                        !(recoveryOwnerAttemptKey is null
                            ? cart.CompleteRecoveryPublication(attemptGuid)
                            : cart.CompleteRecoveryPublication(attemptKey)))
                    {
                        throw new InvalidOperationException(
                            $"Linkly attempt {attemptGuid:D} order was saved but its recovery cart could not be released.");
                    }
                }

                await AcknowledgeCompletedCardAttemptAsync(attemptGuid, cancellationToken);
            }
            catch (Exception ex) when (
                isCapturedOwner &&
                ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 中文注释：订单已落盘但 attempt 未能终态化时，撤回本 attempt 发布的活动购物车，避免双重有效状态。
                if (recoveryOwnerAttemptKey is null)
                {
                    cart.RollbackRecoveryPublication(attemptGuid);
                }
                else
                {
                    cart.RollbackRecoveryPublication(attemptKey);
                }

                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 订单已经耐久保存；一个普通 tender 的收尾告警不能阻止后面的精确 owner 终态化与解锁。
                nonOwnerFailures ??= [];
                nonOwnerFailures.Add(new InvalidOperationException(
                    $"Linkly attempt {attemptGuid:D} post-order finalization failed.",
                    ex));
            }
        }

        if (nonOwnerFailures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more Linkly attempts could not be finalized after the order was saved.",
                nonOwnerFailures);
        }
    }

    private async Task AcknowledgeCompletedCardAttemptAsync(
        Guid attemptGuid,
        CancellationToken cancellationToken)
    {
        var backendTerminalClient = linklyBackendTerminalClient;
        var settingsProvider = cardTerminalSettingsProvider;
        var attemptRepository = cardPaymentAttemptRepository;
        if (backendTerminalClient is null || settingsProvider is null || attemptRepository is null)
        {
            return;
        }

        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attemptGuid, cancellationToken),
            cancellationToken);
        if (attempt is null ||
            attempt.AcknowledgedAt is not null ||
            string.IsNullOrWhiteSpace(attempt.SessionId))
        {
            return;
        }

        var sessionId = attempt.SessionId;
        var settings = await RunLocalStoreAsync(
            () => settingsProvider.GetSettingsAsync(cancellationToken),
            cancellationToken);
        var mode = ResolveAttemptConnectionMode(
            attempt,
            CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode));
        if (settings.Processor != CardProcessorKind.Linkly ||
            mode != LinklyConnectionMode.CloudBackendAsync ||
            !Enum.TryParse<CardTerminalEnvironment>(attempt.Environment, ignoreCase: true, out var environment))
        {
            return;
        }

        try
        {
            // 订单已经落地后才确认 Linkly session，避免恢复列表提前丢失成功交易。
            await backendTerminalClient.AcknowledgeSessionAsync(
                settings with { Environment = environment },
                sessionId,
                cancellationToken);
            await RunLocalStoreAsync(
                () => attemptRepository.MarkAcknowledgedAsync(
                    attemptGuid,
                    DateTimeOffset.UtcNow,
                    cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException)
        {
            ConsoleLog.Write(
                "CardRecovery",
                $"payment completion acknowledge failed attemptGuid={attemptGuid} sessionId={sessionId} error={ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 接管旧 Linkly 活动会话：精确关联权威 attempt、查询至终态、完整证据落库、
    /// 最后 acknowledge/release。落库失败或 ack 失败都返回 Failed，绝不发起新扣款。
    /// </summary>
    private async Task<LinklyActiveSessionTakeoverResult> TakeOverActiveSessionForNewPaymentAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        if (cardPaymentAttemptRepository is null || linklyBackendTerminalClient is null)
        {
            return LinklyActiveSessionTakeoverResult.Failed(
                "The card recovery store is unavailable, so the previous Linkly session could not be cleared.");
        }

        try
        {
            EnsureActiveSessionScopeMatches(settings, session, activeStatus, expectedSessionId: activeStatus.SessionId);

            // 1. 优先关联已有 Sale/Refund/ActiveSession；仅无权威记录时创建 generic ActiveSession。
            var activeAttempt = await FindOrCreateActiveSessionAttemptAsync(
                settings,
                session,
                activeStatus,
                cancellationToken);

            // 2. Resume/query 至确定终态。
            var finalStatus = await linklyBackendTerminalClient.ResumeSessionUntilFinalAsync(
                settings,
                activeStatus,
                cancellationToken);
            if (!IsTerminalActiveSessionStatus(finalStatus))
            {
                return LinklyActiveSessionTakeoverResult.Failed(
                    "The previous Linkly session is still pending and could not be cleared safely.");
            }

            EnsureActiveSessionScopeMatches(settings, session, finalStatus, activeStatus.SessionId);
            if (!string.IsNullOrWhiteSpace(activeStatus.TxnRef) &&
                !string.IsNullOrWhiteSpace(finalStatus.TxnRef) &&
                !string.Equals(
                    activeStatus.TxnRef.Trim(),
                    finalStatus.TxnRef.Trim(),
                    StringComparison.Ordinal))
            {
                return LinklyActiveSessionTakeoverResult.Failed(
                    "The previous Linkly session identity changed during recovery and was not acknowledged.");
            }

            var finalTxnRef = NormalizeOptional(finalStatus.TxnRef) ??
                NormalizeOptional(activeStatus.TxnRef) ??
                NormalizeOptional(activeAttempt.TxnRef);
            var isRefundAttempt = string.Equals(
                activeAttempt.OperationKind,
                "Refund",
                StringComparison.Ordinal);
            var refundSubmissionToken = NormalizeOptional(activeAttempt.SubmissionToken);
            if (isRefundAttempt && refundSubmissionToken is null)
            {
                return LinklyActiveSessionTakeoverResult.Failed(
                    "The previous Linkly refund is missing its submission identity and was not acknowledged.");
            }

            var outcome = MapActiveSessionOutcome(finalStatus);
            var isGenericActiveSession = string.Equals(activeAttempt.OperationKind, "ActiveSession", StringComparison.Ordinal);
            // 旧销售/退款一旦记为 Approved，恢复会把它当作已持久化的金融事实、按草稿金额自动落单，
            // 所以已到终态的批准在这里也必须核验金额；核验不过就不落 Approved、不确认会话，留给恢复流程判为未知。
            // 无草稿的 generic 记录本来就降级为待复核、不会自动落单，仍照常确认以释放终端。
            if (outcome == LocalCardPaymentAttemptStatus.Approved &&
                (!LinklyBackendTerminalClient.HasPendingApprovalEvidenceMatchingAttempt(
                     finalStatus,
                     finalTxnRef,
                     activeAttempt.Amount,
                     activeAttempt.TxnType) ||
                 (!isGenericActiveSession &&
                  !LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(
                      finalStatus,
                      finalTxnRef,
                      activeAttempt.Amount))))
            {
                return LinklyActiveSessionTakeoverResult.Failed(
                    "The previous Linkly approval evidence does not match the persisted transaction and was not acknowledged.");
            }

            if (isGenericActiveSession &&
                outcome == LocalCardPaymentAttemptStatus.Approved)
            {
                // 无订单草稿的 generic 记录不能自动完成旧单；ack 后继续留在异常中心等待主管核实。
                outcome = LocalCardPaymentAttemptStatus.RequiresReview;
            }

            // 3. 完整最终 status/response/session/txn 证据落库成功后才允许 ack。
            var completedAt = DateTimeOffset.UtcNow;
            await RunLocalStoreAsync(
                () => isRefundAttempt
                    ? RequireRefundCasAsync(
                        cardPaymentAttemptRepository.TryUpdateRefundSessionAsync(
                            activeAttempt.AttemptGuid,
                            refundSubmissionToken!,
                            finalStatus.SessionId,
                            finalTxnRef,
                            completedAt,
                            CancellationToken.None))
                    : cardPaymentAttemptRepository.UpdateSessionAsync(
                        activeAttempt.AttemptGuid,
                        finalStatus.SessionId,
                        finalTxnRef,
                        completedAt,
                        CancellationToken.None),
                CancellationToken.None);

            await RunLocalStoreAsync(
                () => isRefundAttempt
                    ? RequireRefundCasAsync(
                        cardPaymentAttemptRepository.TryUpdateRefundOutcomeAsync(
                            activeAttempt.AttemptGuid,
                            refundSubmissionToken!,
                            outcome,
                            finalStatus.ResponseCode,
                            finalStatus.ResponseText,
                            finalTxnRef,
                            completedAt,
                            CancellationToken.None))
                    : cardPaymentAttemptRepository.UpdateOutcomeAsync(
                        activeAttempt.AttemptGuid,
                        outcome,
                        finalStatus.ResponseCode,
                        finalStatus.ResponseText,
                        finalTxnRef,
                        completedAt,
                        CancellationToken.None),
                CancellationToken.None);

            // 4. acknowledge/release；先终端 ack 再本地标记，失败即禁止新扣款。
            await linklyBackendTerminalClient.AcknowledgeSessionAsync(
                settings,
                finalStatus.SessionId,
                cancellationToken);
            await RunLocalStoreAsync(
                () => cardPaymentAttemptRepository.MarkAcknowledgedAsync(
                    activeAttempt.AttemptGuid,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None),
                CancellationToken.None);

            return LinklyActiveSessionTakeoverResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 真实 caller 取消不能被吞为失败；向上传播由 backend client 的异常处理决定语义。
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogCardRecoveryWarning("take-over-active-session", null, ex);
            return LinklyActiveSessionTakeoverResult.Failed(
                "The previous Linkly session could not be cleared safely. Do not charge again until it is resolved.");
        }
    }

    private async Task<LocalCardPaymentAttempt> FindOrCreateActiveSessionAttemptAsync(
        CardTerminalSettings settings,
        PosSessionState session,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(status.SessionId))
        {
            throw new InvalidOperationException("Linkly active session does not contain a SessionId.");
        }

        var openAttempts = await RunLocalStoreAsync(
            () => cardPaymentAttemptRepository!.GetOpenAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                settings.Environment.ToString(),
                CancellationToken.None),
            CancellationToken.None);
        var normalizedSessionId = status.SessionId.Trim();
        var sessionMatches = openAttempts
            .Where(candidate => string.Equals(
                NormalizeOptional(candidate.SessionId),
                normalizedSessionId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (sessionMatches.Length > 1)
        {
            throw new InvalidOperationException("Multiple open Linkly attempts match the active SessionId.");
        }

        if (sessionMatches.Length == 1)
        {
            var matched = sessionMatches[0];
            if (!string.IsNullOrWhiteSpace(status.TxnRef) &&
                !string.IsNullOrWhiteSpace(matched.TxnRef) &&
                !string.Equals(status.TxnRef.Trim(), matched.TxnRef.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The active Linkly SessionId matches an attempt with a different TxnRef.");
            }

            return matched;
        }

        var normalizedTxnRef = NormalizeOptional(status.TxnRef);
        if (normalizedTxnRef is not null)
        {
            var txnMatches = openAttempts
                .Where(candidate => string.Equals(
                    NormalizeOptional(candidate.TxnRef),
                    normalizedTxnRef,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (txnMatches.Length > 1)
            {
                throw new InvalidOperationException("Multiple open Linkly attempts match the active TxnRef.");
            }

            if (txnMatches.Length == 1)
            {
                if (!string.IsNullOrWhiteSpace(txnMatches[0].SessionId))
                {
                    throw new InvalidOperationException(
                        "The active Linkly TxnRef matches an attempt with a different SessionId.");
                }

                return txnMatches[0];
            }

            // 分期付款/还款的 attempt 不在异常中心的未结队列里（它们由分期自己的恢复流程处理）。
            // 后端模式下分期的 TxnRef 在发请求前就由 attempt 身份派生，这里按它精确认领遗留会话，
            // 让接管把会话终态落到分期自己的 attempt 上，而不是另建一条来源不明的 ActiveSession。
            var installmentAttempts = await RunLocalStoreAsync(
                () => cardPaymentAttemptRepository!.GetUnacknowledgedInstallmentAttemptsAsync(
                    session.StoreCode,
                    session.DeviceCode,
                    settings.Environment.ToString(),
                    CancellationToken.None),
                CancellationToken.None);
            var installmentMatches = installmentAttempts
                .Where(candidate =>
                    string.Equals(NormalizeOptional(candidate.TxnRef), normalizedTxnRef, StringComparison.Ordinal) &&
                    (string.IsNullOrWhiteSpace(candidate.SessionId) ||
                     string.Equals(NormalizeOptional(candidate.SessionId), normalizedSessionId, StringComparison.Ordinal)))
                .Take(2)
                .ToArray();
            if (installmentMatches.Length > 1)
            {
                throw new InvalidOperationException("Multiple installment Linkly attempts match the active TxnRef.");
            }

            if (installmentMatches.Length == 1)
            {
                return installmentMatches[0];
            }
        }

        var now = DateTimeOffset.UtcNow;
        var attempt = new LocalCardPaymentAttempt(
            Guid.NewGuid(),
            normalizedSessionId,
            normalizedTxnRef,
            CardProcessorKind.Linkly.ToString(),
            settings.Environment.ToString(),
            nameof(LinklyConnectionMode.CloudBackendAsync),
            "U",
            ToDecimalAmount(status.CardTransaction?.AmountCents),
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
            () => cardPaymentAttemptRepository!.CreateOrGetActiveSessionAsync(attempt, CancellationToken.None),
            CancellationToken.None);
    }

    private static bool IsTerminalActiveSessionStatus(LinklyCloudBackendSessionResponse status)
    {
        return LinklyBackendTerminalClient.IsApprovedFinalTransaction(status) ||
            string.Equals(status.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, "NotSubmitted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, "Canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static LocalCardPaymentAttemptStatus MapActiveSessionOutcome(LinklyCloudBackendSessionResponse status)
    {
        if (LinklyBackendTerminalClient.IsApprovedFinalTransaction(status))
        {
            return LocalCardPaymentAttemptStatus.Approved;
        }

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

    private static void EnsureActiveSessionScopeMatches(
        CardTerminalSettings settings,
        PosSessionState session,
        LinklyCloudBackendSessionResponse status,
        string? expectedSessionId)
    {
        if (string.IsNullOrWhiteSpace(status.SessionId) ||
            !string.Equals(status.Environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(status.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(status.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(expectedSessionId) &&
             !string.Equals(status.SessionId, expectedSessionId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The Linkly active session does not belong to the current store, device, environment, and session identity.");
        }
    }

    private static decimal ToDecimalAmount(long? amountCents)
    {
        return amountCents is long cents ? decimal.Round(cents / 100m, 2, MidpointRounding.AwayFromZero) : 0m;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task MarkCompletedSquareAttemptsAsync(
        PosCartService cart,
        IReadOnlyList<PaymentTender> tenders,
        Guid? recoveryOwnerAttemptGuid,
        CardRecoveryAttemptKey? recoveryOwnerAttemptKey,
        CancellationToken cancellationToken)
    {
        if (squarePaymentAttemptRepository is null)
        {
            return;
        }

        var tenderAttemptGuids = tenders
            .Where(tender => tender.Method == PaymentMethodKind.Card)
            .Select(tender => TryReadSquareAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : (Guid?)null)
            .Where(attemptGuid => attemptGuid is not null)
            .Select(attemptGuid => attemptGuid!.Value)
            .ToHashSet();
        var attemptGuids = new HashSet<Guid>(tenderAttemptGuids);
        var ownerIsLinklyTender = recoveryOwnerAttemptKey is null &&
            recoveryOwnerAttemptGuid is Guid ownerGuid && tenders.Any(tender =>
            tender.Method == PaymentMethodKind.Card &&
            TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var cardAttemptGuid) &&
            cardAttemptGuid == ownerGuid);
        var squareOwnerGuid = recoveryOwnerAttemptKey is CardRecoveryAttemptKey ownerKey
            ? ownerKey.Processor == CardProcessorKind.Square
                ? ownerKey.AttemptGuid
                : (Guid?)null
            : recoveryOwnerAttemptGuid is Guid legacyOwnerGuid && !ownerIsLinklyTender
                ? legacyOwnerGuid
                : null;
        if (squareOwnerGuid is Guid capturedSquareOwnerGuid)
        {
            attemptGuids.Add(capturedSquareOwnerGuid);
        }

        // 已批准 tender 先完成订单映射；owner-only 的失败退款最后收尾并释放 publication。
        List<Exception>? nonOwnerFailures = null;
        foreach (var attemptGuid in attemptGuids.OrderBy(attemptGuid =>
            squareOwnerGuid == attemptGuid ? 1 : 0))
        {
            var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, attemptGuid);
            var isCapturedOwner = IsCapturedRecoveryOwner(
                recoveryOwnerAttemptKey,
                recoveryOwnerAttemptGuid,
                attemptKey);
            try
            {
                var completedAt = DateTimeOffset.UtcNow;
                var attempt = await RunLocalStoreAsync(
                    () => squarePaymentAttemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                    CancellationToken.None);
                var isOwnerOnlyAlternativeRefund =
                    isCapturedOwner &&
                    !tenderAttemptGuids.Contains(attemptGuid) &&
                    (IsAlternativeSquareRefundOwner(attempt) ||
                     IsCompletedAlternativeSquareRefundOwner(attempt));
                if (attempt is not null &&
                    string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal))
                {
                    var targetStatus = attempt.RecoveryTargetStatus;
                    if (targetStatus == LocalSquarePaymentAttemptStatus.Abandoned)
                    {
                        if (!isOwnerOnlyAlternativeRefund)
                        {
                            throw new InvalidOperationException(
                                $"Square attempt {attemptGuid:D} cannot be abandoned by this saved order.");
                        }
                    }
                    else if (targetStatus != LocalSquarePaymentAttemptStatus.OrderCompleted)
                    {
                        throw new InvalidOperationException(
                            $"Square attempt {attemptGuid:D} has an invalid post-order recovery target.");
                    }

                    // 中文注释：恢复草稿对应订单已落盘，此处才用原状态版本完成最终 CAS。
                    var finalized = await RunLocalStoreAsync(
                        () => squarePaymentAttemptRepository.TryCompleteRecoveryFinalizationAsync(
                            attemptGuid,
                            attempt.Status,
                            attempt.UpdatedAt,
                            targetStatus.Value,
                            completedAt,
                            CancellationToken.None),
                        CancellationToken.None);
                    if (!finalized)
                    {
                        var winner = await RunLocalStoreAsync(
                            () => squarePaymentAttemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                            CancellationToken.None);
                        if (winner is null ||
                            winner.Status != targetStatus ||
                            !string.Equals(winner.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Square attempt {attemptGuid:D} order was saved but recovery finalization is still pending.");
                        }
                    }
                }
                else
                {
                    if (isCapturedOwner && !tenderAttemptGuids.Contains(attemptGuid))
                    {
                        if (!IsCompletedAlternativeSquareRefundOwner(attempt))
                        {
                            throw new InvalidOperationException(
                                $"Square recovery owner {attemptGuid:D} is not awaiting alternative-refund finalization.");
                        }
                    }
                    else
                    {
                        // 只有订单真正保存后，普通 Square attempt 才能标为完成，避免恢复时漏救订单。
                        await RunLocalStoreAsync(
                            () => squarePaymentAttemptRepository.MarkOrderCompletedAsync(
                                attemptGuid,
                                completedAt,
                                CancellationToken.None),
                            CancellationToken.None);
                    }
                }

                if (isCapturedOwner)
                {
                    // 中文注释：终态已确认后才释放 Square 恢复购物车，防止订单与活动草稿同时有效。
                    var completedAttempt = await RunLocalStoreAsync(
                        () => squarePaymentAttemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                        CancellationToken.None);
                    var expectedOwnerStatus = isOwnerOnlyAlternativeRefund
                        ? LocalSquarePaymentAttemptStatus.Abandoned
                        : LocalSquarePaymentAttemptStatus.OrderCompleted;
                    if (completedAttempt is null ||
                        completedAttempt.Status != expectedOwnerStatus ||
                        !string.Equals(completedAttempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) ||
                        !(recoveryOwnerAttemptKey is null
                            ? cart.CompleteRecoveryPublication(attemptGuid)
                            : cart.CompleteRecoveryPublication(attemptKey)))
                    {
                        throw new InvalidOperationException(
                            $"Square attempt {attemptGuid:D} order was saved but its recovery cart could not be released.");
                    }
                }
            }
            catch (Exception ex) when (
                isCapturedOwner &&
                ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 中文注释：最终 CAS 失败时只撤回当前 attempt 发布的草稿，保留 FinalizePending 供下次重放。
                if (recoveryOwnerAttemptKey is null)
                {
                    cart.RollbackRecoveryPublication(attemptGuid);
                }
                else
                {
                    cart.RollbackRecoveryPublication(attemptKey);
                }

                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 非 owner 的告警留给异常中心重放，但不能让已保存的替代退款 owner 永久停在空购物车上。
                nonOwnerFailures ??= [];
                nonOwnerFailures.Add(new InvalidOperationException(
                    $"Square attempt {attemptGuid:D} post-order finalization failed.",
                    ex));
            }
        }

        if (nonOwnerFailures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more Square attempts could not be finalized after the order was saved.",
                nonOwnerFailures);
        }
    }

    private static async Task<PaymentTenderAttemptResult> AuthorizeExternalTenderAsync(
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        string? referenceText,
        CancellationToken cancellationToken,
        Func<decimal, PosSessionState, string?, CancellationToken, Task<PaymentAuthorizationResult>> authorizeAsync,
        PaymentMethodKind method,
        string exceedsRemainingStatusKey,
        string declinedStatusKey,
        string approvedStatusKey)
    {
        if (amount > remainingAmount)
        {
            if (method == PaymentMethodKind.Card)
            {
                ConsoleLog.Write(
                    "CardRefund",
                    $"workflow blocked card refund reason=amount-exceeds-remaining amount={amount:0.00} remaining={remainingAmount:0.00} originalReference={LogValue(referenceText)}");
            }

            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        if (method == PaymentMethodKind.Card)
        {
            var operation = string.IsNullOrWhiteSpace(referenceText) ? "payment" : "refund";
            ConsoleLog.Write(
                "CardRefund",
                $"workflow terminal {operation} start amount={amount:0.00} remaining={remainingAmount:0.00} originalReference={LogValue(referenceText)}");
        }

        var authorization = await authorizeAsync(amount, session, referenceText, cancellationToken);
        if (method == PaymentMethodKind.Card)
        {
            var operation = string.IsNullOrWhiteSpace(referenceText) ? "payment" : "refund";
            ConsoleLog.Write(
                "CardRefund",
                $"workflow terminal {operation} completed approved={authorization.Approved} reference={LogValue(authorization.Reference)} " +
                $"message={LogValue(authorization.Message)} authorizedAmount={authorization.AuthorizedAmount?.ToString("0.00") ?? "<null>"} " +
                $"cardTxCount={authorization.CardTransactions?.Count ?? 0}");
        }
        else if (method == PaymentMethodKind.Voucher)
        {
            // 代金券锁定结果：批准时 Message 里是券号、Reference 含 reservationToken，二者都不进日志，只记券号后 4 位。
            ConsoleLog.WriteInformation(
                "Voucher",
                $"voucher tender authorize completed approved={authorization.Approved} voucherTail={VoucherCodeLogFormat.Tail(referenceText)} " +
                $"requestedAmount={amount:0.00} authorizedAmount={authorization.AuthorizedAmount?.ToString("0.00") ?? "<null>"} " +
                $"statusKey={LogValue(authorization.StatusKey)}",
                new ApplicationLogContext(Properties: new Dictionary<string, object?>
                {
                    ["storeCode"] = session.StoreCode,
                    ["deviceCode"] = session.DeviceCode,
                    ["result"] = authorization.Approved ? "approved" : "declined"
                }));
        }

        if (!authorization.Approved)
        {
            return PaymentTenderAttemptResult.Fail(
                string.IsNullOrWhiteSpace(authorization.StatusKey) ? declinedStatusKey : authorization.StatusKey,
                authorization.Message);
        }

        var authorizedAmount = decimal.Round(
            authorization.AuthorizedAmount ?? amount,
            2,
            MidpointRounding.AwayFromZero);
        if (authorizedAmount <= 0m)
        {
            return PaymentTenderAttemptResult.Fail(declinedStatusKey, authorization.Message);
        }

        if (authorizedAmount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        if (method == PaymentMethodKind.Card && authorizedAmount != amount)
        {
            return PaymentTenderAttemptResult.Fail(
                declinedStatusKey,
                "Card terminal authorized amount did not match the requested amount.");
        }

        return PaymentTenderAttemptResult.Success(
            new PaymentTender(method, authorizedAmount, authorization.Reference, CardTransactions: authorization.CardTransactions),
            approvedStatusKey);
    }

    private static async Task<PaymentTenderAttemptResult> AuthorizeRefundTenderAsync(
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        string? referenceText,
        CancellationToken cancellationToken,
        Func<decimal, PosSessionState, string?, CancellationToken, Task<PaymentAuthorizationResult>> authorizeAsync,
        PaymentMethodKind method,
        string exceedsRemainingStatusKey,
        string declinedStatusKey,
        string approvedStatusKey)
    {
        if (amount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        var authorization = await authorizeAsync(amount, session, referenceText, cancellationToken);
        if (!authorization.Approved)
        {
            return PaymentTenderAttemptResult.Fail(
                declinedStatusKey,
                authorization.Message);
        }

        var authorizedAmount = decimal.Round(
            authorization.AuthorizedAmount ?? amount,
            2,
            MidpointRounding.AwayFromZero);
        if (authorizedAmount <= 0m)
        {
            return PaymentTenderAttemptResult.Fail(declinedStatusKey, authorization.Message);
        }

        if (authorizedAmount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        if (method == PaymentMethodKind.Card && authorizedAmount != amount)
        {
            return PaymentTenderAttemptResult.Fail(
                declinedStatusKey,
                "Card terminal authorized amount did not match the requested amount.");
        }

        var reference = method == PaymentMethodKind.Card
            ? CardRefundReference.Format(authorization.Reference, referenceText!)
            : authorization.Reference;
        return PaymentTenderAttemptResult.Success(
            new PaymentTender(method, -authorizedAmount, reference, CardTransactions: authorization.CardTransactions),
            approvedStatusKey);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static PaymentTenderAttemptResult AuthorizeRefundTenderAsync(
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        string? referenceText,
        CancellationToken cancellationToken,
        PaymentMethodKind method,
        string exceedsRemainingStatusKey,
        string approvedStatusKey)
    {
        _ = session;
        _ = referenceText;
        cancellationToken.ThrowIfCancellationRequested();

        if (amount > remainingAmount)
        {
            return PaymentTenderAttemptResult.Fail(exceedsRemainingStatusKey);
        }

        return PaymentTenderAttemptResult.Success(
            new PaymentTender(method, -RoundCurrency(amount), "VOUCHER_REFUND_PENDING", IdempotencyKey: Guid.NewGuid().ToString("N")),
            approvedStatusKey);
    }

    private static LocalOrder PrepareOrderForVoucherRefundPersistence(LocalOrder order)
    {
        var updatedPayments = new List<LocalPayment>(order.Payments.Count);
        var changed = false;

        foreach (var payment in order.Payments)
        {
            if (!IsVoucherRefundPayment(payment))
            {
                updatedPayments.Add(payment);
                continue;
            }

            var normalizedIdempotencyKey = EnsureRefundVoucherIdempotencyKey(order.OrderGuid, payment);
            var normalizedReference = HasIssuedVoucherRefundReference(payment.Reference)
                ? payment.Reference?.Trim()
                : "VOUCHER_REFUND_PENDING";
            var updatedPayment = payment with
            {
                Reference = normalizedReference,
                IdempotencyKey = normalizedIdempotencyKey
            };
            updatedPayments.Add(updatedPayment);
            changed |= !Equals(updatedPayment, payment);
        }

        return changed
            ? order with { Payments = updatedPayments }
            : order;
    }

    private async Task<RecoverableOrderPersistence> PrepareOrderForRecoverableCardPersistenceAsync(
        LocalOrder order,
        IReadOnlyList<PaymentTender> tenders,
        Guid? recoveryOwnerAttemptGuid,
        CardRecoveryAttemptKey? recoveryOwnerAttemptKey,
        CancellationToken cancellationToken)
    {
        var manualTenders = tenders.Where(tender =>
            ManualCardPaymentReference.IsManual(tender.Reference) ||
            tender.CardTransactions?.Any(transaction => string.Equals(transaction.Processor,
                ManualCardPaymentReference.Processor, StringComparison.OrdinalIgnoreCase)) == true).ToArray();
        if (manualTenders.Length > 0)
        {
            var manual = manualTenders[0];
            if (manualTenders.Length != 1 || manual.Method != PaymentMethodKind.Card ||
                order.ActualAmount <= 0m || order.Lines.Any(line => line.Kind == OrderLineKind.Return) ||
                recoveryOwnerAttemptGuid is not null || recoveryOwnerAttemptKey is not null ||
                tenders.Count(tender => tender.Method == PaymentMethodKind.Card) != 1 ||
                !ManualCardPaymentReference.TryParse(manual.Reference, out var confirmationId) ||
                !string.Equals(manual.Reference, manual.IdempotencyKey, StringComparison.Ordinal) ||
                manual.CardTransactions is not { Count: 1 } ||
                manual.CardTransactions[0].Processor != ManualCardPaymentReference.Processor ||
                manual.CardTransactions[0].Amount != manual.Amount ||
                manual.CardTransactions[0].TxnRef != manual.Reference)
            {
                throw new InvalidOperationException("The manually confirmed payment identity is inconsistent.");
            }

            // 中文注释：同一次人工确认始终对应同一订单，保存返回未知时重试不会再新增订单或调用刷卡机。
            var manualOrder = order with { OrderGuid = confirmationId };
            var persistedOrder = await orderRepository.GetOrderAsync(confirmationId, cancellationToken);
            if (persistedOrder is null)
            {
                return new RecoverableOrderPersistence(manualOrder, AlreadyPersisted: false);
            }

            if (!MatchesManualPaymentContinuation(manualOrder, persistedOrder))
            {
                throw new InvalidOperationException("The saved order does not match the manually confirmed payment.");
            }

            return new RecoverableOrderPersistence(persistedOrder, AlreadyPersisted: true);
        }

        if (recoveryOwnerAttemptKey is CardRecoveryAttemptKey exactOwnerKey)
        {
            if (recoveryOwnerAttemptGuid != exactOwnerKey.AttemptGuid)
            {
                throw new InvalidOperationException("The recovery cart owner identity is inconsistent.");
            }

            if (exactOwnerKey.Processor == CardProcessorKind.Square)
            {
                var ownerAttempt = squarePaymentAttemptRepository is null
                    ? null
                    : await RunLocalStoreAsync(
                        () => squarePaymentAttemptRepository.GetAttemptAsync(
                            exactOwnerKey.AttemptGuid,
                            cancellationToken),
                        cancellationToken);
                if (ownerAttempt is null)
                {
                    throw new InvalidOperationException(
                        $"Square recovery owner {exactOwnerKey.AttemptGuid:D} was not found.");
                }

                var ownerHasSquareTender = tenders.Any(tender =>
                    tender.Method == PaymentMethodKind.Card &&
                    TryReadSquareAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) &&
                    attemptGuid == exactOwnerKey.AttemptGuid);
                if (IsAlternativeSquareRefundOwner(ownerAttempt))
                {
                    if (ownerHasSquareTender)
                    {
                        throw new InvalidOperationException(
                            $"Square alternative-refund owner {exactOwnerKey.AttemptGuid:D} cannot be represented by a card tender.");
                    }

                    return await PrepareAlternativeSquareRefundPersistenceAsync(
                        order,
                        tenders,
                        ownerAttempt,
                        cancellationToken);
                }

                if (IsCompletedAlternativeSquareRefundOwner(ownerAttempt) || !ownerHasSquareTender)
                {
                    throw new InvalidOperationException(
                        $"Square recovery owner {exactOwnerKey.AttemptGuid:D} has an invalid card-tender identity.");
                }

                var ownerDraft = DeserializeRequiredCardPaymentOrderDraft(ownerAttempt);
                return new RecoverableOrderPersistence(
                    order with { OrderGuid = ownerDraft.OrderGuid },
                    AlreadyPersisted: false);
            }

            if (exactOwnerKey.Processor == CardProcessorKind.Linkly)
            {
                var ownerHasLinklyTender = tenders.Any(tender =>
                    tender.Method == PaymentMethodKind.Card &&
                    TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) &&
                    attemptGuid == exactOwnerKey.AttemptGuid);
                var ownerAttempt = cardPaymentAttemptRepository is null
                    ? null
                    : await RunLocalStoreAsync(
                        () => cardPaymentAttemptRepository.GetAttemptAsync(
                            exactOwnerKey.AttemptGuid,
                            cancellationToken),
                        cancellationToken);
                if (!ownerHasLinklyTender || ownerAttempt is null)
                {
                    throw new InvalidOperationException(
                        $"Linkly recovery owner {exactOwnerKey.AttemptGuid:D} has an invalid card-tender identity.");
                }

                var ownerDraft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
                    ownerAttempt.OrderDraftJson,
                    CardAttemptJsonOptions);
                if (ownerDraft is null || ownerDraft.OrderGuid == Guid.Empty)
                {
                    throw new InvalidOperationException("The Linkly recovery owner contains an invalid order draft.");
                }

                return new RecoverableOrderPersistence(
                    order with { OrderGuid = ownerDraft.OrderGuid },
                    AlreadyPersisted: false);
            }

            throw new InvalidOperationException(
                $"Recovery owner provider {exactOwnerKey.Processor} is not supported.");
        }

        if (recoveryOwnerAttemptGuid is Guid ownerAttemptGuid)
        {
            var ownerHasSquareTender = tenders.Any(tender =>
                tender.Method == PaymentMethodKind.Card &&
                TryReadSquareAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) &&
                attemptGuid == ownerAttemptGuid);
            var ownerHasLinklyTender = tenders.Any(tender =>
                tender.Method == PaymentMethodKind.Card &&
                TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) &&
                attemptGuid == ownerAttemptGuid);

            if (ownerHasSquareTender)
            {
                var representedOwner = squarePaymentAttemptRepository is null
                    ? null
                    : await RunLocalStoreAsync(
                        () => squarePaymentAttemptRepository.GetAttemptAsync(ownerAttemptGuid, cancellationToken),
                        cancellationToken);
                if (representedOwner is null ||
                    IsAlternativeSquareRefundOwner(representedOwner) ||
                    IsCompletedAlternativeSquareRefundOwner(representedOwner))
                {
                    throw new InvalidOperationException(
                        $"Square recovery owner {ownerAttemptGuid:D} has an invalid card-tender identity.");
                }
            }

            if (!ownerHasSquareTender && !ownerHasLinklyTender)
            {
                var ownerAttempt = squarePaymentAttemptRepository is null
                    ? null
                    : await RunLocalStoreAsync(
                        () => squarePaymentAttemptRepository.GetAttemptAsync(ownerAttemptGuid, cancellationToken),
                        cancellationToken);
                if (!IsAlternativeSquareRefundOwner(ownerAttempt))
                {
                    throw new InvalidOperationException(
                        $"Recovery owner {ownerAttemptGuid:D} does not identify an open Square alternative refund.");
                }

                return await PrepareAlternativeSquareRefundPersistenceAsync(
                    order,
                    tenders,
                    ownerAttempt!,
                    cancellationToken);
            }
        }

        var attemptGuid = tenders
            .Select(tender => TryReadCardAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : (Guid?)null)
            .FirstOrDefault(value => value is not null);
        if (attemptGuid is not null && cardPaymentAttemptRepository is not null)
        {
            var attempt = await RunLocalStoreAsync(
                () => cardPaymentAttemptRepository.GetAttemptAsync(attemptGuid.Value, cancellationToken),
                cancellationToken);
            if (attempt is not null)
            {
                var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, CardAttemptJsonOptions);
                // 正常落单和重启恢复必须使用同一个订单 GUID，避免崩溃后恢复重复保存订单。
                return new RecoverableOrderPersistence(
                    draft is null ? order : order with { OrderGuid = draft.OrderGuid },
                    AlreadyPersisted: false);
            }
        }

        var squareAttemptGuid = tenders
            .Select(tender => TryReadSquareAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : (Guid?)null)
            .FirstOrDefault(value => value is not null);
        if (squareAttemptGuid is not null && squarePaymentAttemptRepository is not null)
        {
            var attempt = await RunLocalStoreAsync(
                () => squarePaymentAttemptRepository.GetAttemptAsync(squareAttemptGuid.Value, cancellationToken),
                cancellationToken);
            if (attempt is not null)
            {
                var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, CardAttemptJsonOptions);
                // Square 使用独立 attempt 表，但订单 GUID 仍必须和刷卡前草稿保持一致。
                return new RecoverableOrderPersistence(
                    draft is null ? order : order with { OrderGuid = draft.OrderGuid },
                    AlreadyPersisted: false);
            }
        }

        return new RecoverableOrderPersistence(order, AlreadyPersisted: false);
    }

    private static bool MatchesManualPaymentContinuation(LocalOrder candidate, LocalOrder persisted)
    {
        if (candidate.OrderGuid != persisted.OrderGuid || candidate.StoreCode != persisted.StoreCode ||
            candidate.DeviceCode != persisted.DeviceCode || candidate.CashierId != persisted.CashierId ||
            candidate.CashierName != persisted.CashierName || candidate.TotalAmount != persisted.TotalAmount ||
            candidate.DiscountAmount != persisted.DiscountAmount || candidate.ActualAmount != persisted.ActualAmount ||
            candidate.TenderedAmount != persisted.TenderedAmount || candidate.ChangeAmount != persisted.ChangeAmount ||
            candidate.Lines.Count != persisted.Lines.Count || candidate.Payments.Count != persisted.Payments.Count)
        {
            return false;
        }

        // 中文注释：重试的临时行/付款 GUID 和时间会重新生成；商品、付款及人工确认身份必须逐项相同。
        for (var index = 0; index < candidate.Lines.Count; index++)
        {
            if (candidate.Lines[index] with { OrderLineGuid = Guid.Empty } !=
                persisted.Lines[index] with { OrderLineGuid = Guid.Empty })
            {
                return false;
            }
        }

        for (var index = 0; index < candidate.Payments.Count; index++)
        {
            var left = candidate.Payments[index];
            var right = persisted.Payments[index];
            if (left.Method != right.Method || left.Amount != right.Amount || left.Reference != right.Reference ||
                left.IdempotencyKey != right.IdempotencyKey ||
                !(left.CardTransactions ?? []).SequenceEqual(right.CardTransactions ?? []))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<RecoverableOrderPersistence> PrepareAlternativeSquareRefundPersistenceAsync(
        LocalOrder order,
        IReadOnlyList<PaymentTender> tenders,
        LocalSquarePaymentAttempt ownerAttempt,
        CancellationToken cancellationToken)
    {
        var ownerDraft = DeserializeRequiredCardPaymentOrderDraft(ownerAttempt);
        EnsureAlternativeRefundTenderContinuation(ownerDraft, tenders);
        var recoveredOrder = order with { OrderGuid = ownerDraft.OrderGuid };
        // 首次落单与重启收尾使用同一套身份校验；不能只凭相同金额和 tender 前缀占用草稿 GUID。
        if (!SquarePaymentRecoveryService.MatchesPersistedAlternativeRefundOrder(
                ownerAttempt,
                ownerDraft,
                recoveredOrder))
        {
            throw new InvalidOperationException(
                "The alternative refund order does not match its durable recovery draft.");
        }

        var existingOrder = await RunLocalStoreAsync(
            () => orderRepository.GetOrderAsync(ownerDraft.OrderGuid, cancellationToken),
            cancellationToken);
        if (existingOrder is null)
        {
            return new RecoverableOrderPersistence(recoveredOrder, AlreadyPersisted: false);
        }

        // 中文注释：发券前崩溃后必须复用已保存订单及其 PaymentGuid/幂等键；
        // 同 GUID 但 tender 身份不同则失败关闭，绝不能插入第二张退款订单。
        if (!SquarePaymentRecoveryService.MatchesPersistedAlternativeRefundOrder(
                ownerAttempt,
                ownerDraft,
                existingOrder) ||
            !MatchesPersistedAlternativeRefundContinuation(recoveredOrder, existingOrder))
        {
            throw new InvalidOperationException(
                "The saved alternative refund order does not match the recovered payment continuation.");
        }

        return new RecoverableOrderPersistence(existingOrder, AlreadyPersisted: true);
    }

    private static bool MatchesPersistedAlternativeRefundContinuation(
        LocalOrder recoveredOrder,
        LocalOrder persistedOrder)
    {
        if (recoveredOrder.Payments.Count != persistedOrder.Payments.Count)
        {
            return false;
        }

        for (var index = 0; index < recoveredOrder.Payments.Count; index++)
        {
            var recovered = recoveredOrder.Payments[index];
            var persisted = persistedOrder.Payments[index];
            var recoveredReference = NormalizeOptional(recovered.Reference);
            var persistedReference = NormalizeOptional(persisted.Reference);
            var referenceMatches = string.Equals(
                    recoveredReference,
                    persistedReference,
                    StringComparison.Ordinal) ||
                recovered.Method == PaymentMethodKind.Voucher &&
                string.Equals(
                    recoveredReference,
                    "VOUCHER_REFUND_PENDING",
                    StringComparison.OrdinalIgnoreCase) &&
                HasIssuedVoucherRefundReference(persistedReference);
            if (recovered.Method != persisted.Method ||
                RoundCurrency(recovered.Amount) != RoundCurrency(persisted.Amount) ||
                !referenceMatches ||
                !string.Equals(
                    NormalizeOptional(recovered.IdempotencyKey),
                    NormalizeOptional(persisted.IdempotencyKey),
                    StringComparison.Ordinal) ||
                !(recovered.CardTransactions ?? []).SequenceEqual(persisted.CardTransactions ?? []))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAlternativeSquareRefundOwner(LocalSquarePaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        attempt.Status == LocalSquarePaymentAttemptStatus.Unknown &&
        IsTerminalSquareRefundFailure(attempt.PaymentStatus) &&
        string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) &&
        attempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.Abandoned;

    private static bool IsCompletedAlternativeSquareRefundOwner(LocalSquarePaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        IsTerminalSquareRefundFailure(attempt.PaymentStatus) &&
        attempt.Status == LocalSquarePaymentAttemptStatus.Abandoned &&
        string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) &&
        attempt.RecoveryTargetStatus is null;

    private static CardPaymentOrderDraft DeserializeRequiredCardPaymentOrderDraft(
        LocalSquarePaymentAttempt attempt)
    {
        CardPaymentOrderDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, CardAttemptJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The Square recovery owner contains an invalid order draft.", ex);
        }

        if (draft is null ||
            draft.OrderGuid == Guid.Empty ||
            draft.CurrentTenders is null ||
            draft.CartSnapshot?.Lines is not { Count: > 0 })
        {
            throw new InvalidOperationException("The Square recovery owner contains an incomplete order draft.");
        }

        return draft;
    }

    private static void EnsureAlternativeRefundTenderContinuation(
        CardPaymentOrderDraft draft,
        IReadOnlyList<PaymentTender> currentTenders)
    {
        if (currentTenders.Count < draft.CurrentTenders.Count)
        {
            throw new InvalidOperationException("Recovered tenders are missing from the alternative refund order.");
        }

        for (var index = 0; index < draft.CurrentTenders.Count; index++)
        {
            if (!RecoveryTenderEquals(draft.CurrentTenders[index], currentTenders[index]))
            {
                throw new InvalidOperationException(
                    "Recovered tenders do not match the durable alternative refund draft.");
            }
        }

        if (currentTenders.Skip(draft.CurrentTenders.Count).Any(tender =>
            tender.Method is not (PaymentMethodKind.Cash or PaymentMethodKind.Voucher)))
        {
            throw new InvalidOperationException(
                "Only cash or voucher may complete a failed Square refund recovery.");
        }
    }

    private static bool RecoveryTenderEquals(PaymentTender expected, PaymentTender actual) =>
        expected.Method == actual.Method &&
        decimal.Round(expected.Amount, 2, MidpointRounding.AwayFromZero) ==
            decimal.Round(actual.Amount, 2, MidpointRounding.AwayFromZero) &&
        string.Equals(NormalizeOptional(expected.Reference), NormalizeOptional(actual.Reference), StringComparison.Ordinal) &&
        string.Equals(NormalizeOptional(expected.DisplayLabel), NormalizeOptional(actual.DisplayLabel), StringComparison.Ordinal) &&
        string.Equals(NormalizeOptional(expected.IdempotencyKey), NormalizeOptional(actual.IdempotencyKey), StringComparison.Ordinal) &&
        (expected.CardTransactions ?? []).SequenceEqual(actual.CardTransactions ?? []);

    private async Task<LocalOrder> IssuePendingRefundVouchersAsync(
        LocalOrder order,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var updatedPayments = new List<LocalPayment>(order.Payments.Count);
        var changed = false;

        foreach (var payment in order.Payments)
        {
            if (!IsPendingVoucherRefundPayment(payment))
            {
                updatedPayments.Add(payment);
                continue;
            }

            var idempotencyKey = EnsureRefundVoucherIdempotencyKey(order.OrderGuid, payment);
            var refundLogContext = new ApplicationLogContext(
                TraceId: order.OrderGuid.ToString("D"),
                Properties: new Dictionary<string, object?>
                {
                    ["storeCode"] = session.StoreCode,
                    ["deviceCode"] = session.DeviceCode
                });
            ConsoleLog.WriteInformation(
                "Voucher",
                $"refund voucher issue start orderGuid={order.OrderGuid:D} paymentGuid={payment.PaymentGuid:D} amount={Math.Abs(payment.Amount):0.00}",
                refundLogContext);
            var authorization = await _voucherTenderClient.IssueRefundAsync(
                Math.Abs(payment.Amount),
                session,
                order.OrderGuid.ToString("D"),
                idempotencyKey,
                "Refund",
                cancellationToken);
            if (!authorization.Approved || string.IsNullOrWhiteSpace(authorization.Reference))
            {
                // 退款券未发出：这笔退款仍待处理，需要收银员/人工跟进。
                ConsoleLog.WriteWarning(
                    "Voucher",
                    $"refund voucher issue declined orderGuid={order.OrderGuid:D} paymentGuid={payment.PaymentGuid:D} " +
                    $"amount={Math.Abs(payment.Amount):0.00}",
                    refundLogContext);
                throw new InvalidOperationException(authorization.Message ?? "Voucher refund issuing failed.");
            }

            ConsoleLog.WriteInformation(
                "Voucher",
                $"refund voucher issued orderGuid={order.OrderGuid:D} paymentGuid={payment.PaymentGuid:D} " +
                $"amount={authorization.AuthorizedAmount?.ToString("0.00") ?? Math.Abs(payment.Amount).ToString("0.00")} " +
                $"voucherTail={VoucherCodeLogFormat.Tail(OrderUploadService.ParseVoucherReference(authorization.Reference).VoucherCode)}",
                refundLogContext);

            // 每张退款券发券成功后立刻回写本地引用，避免后续步骤失败时再次展示为待处理。
            await RunLocalStoreAsync(
                () => orderRepository.UpdatePaymentReferenceAsync(payment.PaymentGuid, authorization.Reference, cancellationToken),
                cancellationToken);
            updatedPayments.Add(payment with
            {
                Reference = authorization.Reference,
                IdempotencyKey = idempotencyKey
            });
            changed = true;
        }

        return changed
            ? order with { Payments = updatedPayments }
            : order;
    }

    private static bool IsVoucherRefundPayment(LocalPayment payment)
    {
        return payment.Method == PaymentMethodKind.Voucher && payment.Amount < 0m;
    }

    private static bool IsPendingVoucherRefundPayment(LocalPayment payment)
    {
        return IsVoucherRefundPayment(payment) && !HasIssuedVoucherRefundReference(payment.Reference);
    }

    private static bool HasIssuedVoucherRefundReference(string? reference)
    {
        return !string.IsNullOrWhiteSpace(reference) &&
            !string.Equals(reference.Trim(), "VOUCHER_REFUND_PENDING", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureRefundVoucherIdempotencyKey(Guid orderGuid, LocalPayment payment)
    {
        return string.IsNullOrWhiteSpace(payment.IdempotencyKey)
            ? $"{orderGuid:D}:{payment.PaymentGuid:D}"
            : payment.IdempotencyKey.Trim();
    }

    private static bool HasExistingVoucherTender(
        IReadOnlyList<PaymentTender> currentTenders,
        string? voucherCode)
    {
        var normalizedVoucherCode = NormalizeVoucherCode(voucherCode);
        if (string.IsNullOrWhiteSpace(normalizedVoucherCode))
        {
            return false;
        }

        return currentTenders
            .Where(tender => tender.Method == PaymentMethodKind.Voucher)
            .Select(tender => NormalizeVoucherCode(ParseVoucherCodeFromReference(tender.Reference)))
            .Any(existing => string.Equals(existing, normalizedVoucherCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ParseVoucherCodeFromReference(string? reference)
    {
        var parts = (reference ?? string.Empty).Split(':', StringSplitOptions.TrimEntries);
        return parts.Length >= 2 &&
            (parts[0].Equals("VOUCHER", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("VOUCHER_REFUND", StringComparison.OrdinalIgnoreCase))
                ? parts[1]
                : reference;
    }

    private static (string? VoucherCode, string? ReservationToken) ParseVoucherReservationFromReference(string? reference)
    {
        var parts = (reference ?? string.Empty).Split(':', StringSplitOptions.TrimEntries);
        return parts.Length >= 3 && parts[0].Equals("VOUCHER", StringComparison.OrdinalIgnoreCase)
            ? (parts[1], parts[2])
            : (null, null);
    }

    private static string? NormalizeVoucherCode(string? voucherCode)
    {
        return string.IsNullOrWhiteSpace(voucherCode) ? null : voucherCode.Trim();
    }

    private PaymentTenderAttemptResult CreateCashTenderAttempt(decimal amount)
    {
        var normalizedAmount = _cashRoundingPolicy.NormalizeCashTender(amount);
        return normalizedAmount <= 0m
            ? PaymentTenderAttemptResult.Fail("payment.status.invalidAmount")
            : PaymentTenderAttemptResult.Success(
                new PaymentTender(PaymentMethodKind.Cash, normalizedAmount),
                "payment.status.cashTenderAdded");
    }

    private PaymentTenderAttemptResult CreateRefundCashTenderAttempt(decimal amount)
    {
        var normalizedAmount = _cashRoundingPolicy.NormalizeCashTender(amount);
        return normalizedAmount <= 0m
            ? PaymentTenderAttemptResult.Fail("payment.status.invalidAmount")
            : PaymentTenderAttemptResult.Success(
                new PaymentTender(PaymentMethodKind.Cash, -normalizedAmount),
                "payment.status.cashTenderAdded");
    }

    private decimal CalculateExternalRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> currentTenders)
    {
        var remaining = RoundCurrency(RoundCurrency(actualAmount) - CalculateTenderedAmountForActualBalance(currentTenders));
        return Math.Abs(remaining);
    }

    private decimal CalculateTenderedAmountForActualBalance(IReadOnlyList<PaymentTender> tenders)
    {
        return RoundCurrency(tenders.Sum(tender => NormalizeTender(tender).Amount));
    }

    private PaymentTender NormalizeTender(PaymentTender tender)
    {
        var normalizedAmount = tender.Method == PaymentMethodKind.Cash
            ? NormalizeCashTender(tender.Amount)
            : RoundCurrency(tender.Amount);
        return tender with { Amount = normalizedAmount };
    }

    private decimal CalculateRefundRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders)
    {
        var normalizedTenders = tenders.Select(NormalizeTender).ToList();
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var cashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        if (cashTotal >= 0m)
        {
            return RoundCurrency(actualAmount - nonCashTotal);
        }

        var roundedCashRefund = _cashRoundingPolicy.CalculateRoundedCashDue(Math.Abs(actualAmount), Math.Abs(nonCashTotal));
        return RoundCurrency(cashTotal + roundedCashRefund);
    }

    private decimal NormalizeCashTender(decimal amount)
    {
        return amount < 0m
            ? -_cashRoundingPolicy.NormalizeCashTender(Math.Abs(amount))
            : _cashRoundingPolicy.NormalizeCashTender(amount);
    }

    private static decimal RoundCurrency(decimal amount)
    {
        return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    private static bool HasTerminalDeclineEvidence(PaymentAuthorizationResult authorization)
    {
        // 只有终端或银行真实返回的代码/文案才触发“银行未批准”弹窗，避免本地校验错误被误报为银行拒付。
        return !string.IsNullOrWhiteSpace(authorization.ResponseCode) ||
            !string.IsNullOrWhiteSpace(authorization.ResponseText) ||
            authorization.CardTransactions?.Any(HasCardTransactionResponseEvidence) == true;
    }

    private static bool HasCardTransactionResponseEvidence(CardTransactionDto transaction)
    {
        return !string.IsNullOrWhiteSpace(transaction.ResponseCode) ||
            !string.IsNullOrWhiteSpace(transaction.ResponseText);
    }

    private static string T(string key)
    {
        return LocalizationResourceProvider.Instance[key];
    }

    private static string FormatLinklyModeDisplayName(string? modeText)
    {
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(modeText, LinklyConnectionMode.LocalIp);
        var key = mode switch
        {
            LinklyConnectionMode.CloudDirectSync => "settings.linkly.mode.cloudDirectSync",
            LinklyConnectionMode.CloudBackendAsync => "settings.linkly.mode.cloudBackendAsync",
            _ => "settings.linkly.mode.localIp"
        };

        // 支付页提示面向收银员，不能暴露 CloudBackendAsync 这类内部配置值。
        return T(key);
    }
}

public interface ICardTerminalClient
{
    Task<PaymentAuthorizationResult> AuthorizeAsync(
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        string? originalReference,
        CancellationToken cancellationToken = default);
}

// 中文注释：统一 Tender 工作流已冻结本次卡交易设置时，必须显式传入同一快照，禁止终端路由再次读取可变配置。
internal interface ICardTerminalSettingsBoundClient
{
    Task<PaymentAuthorizationResult> AuthorizeWithSettingsAsync(
        CardTerminalSettings settings,
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RefundWithSettingsAsync(
        CardTerminalSettings settings,
        decimal amount,
        PosSessionState session,
        string? originalReference,
        string? idempotencyKey,
        CancellationToken cancellationToken = default);
}

/// <summary>供需要跨重启重放退款的金融流程传入已持久化幂等键。</summary>
public interface IIdempotentCardRefundClient
{
    Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        string? originalReference,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}

// 中文注释：分期恢复只能查询已持久化的终端 attempt，绝不能再次发起收款。
public interface IInstallmentTerminalRecoveryClient
{
    Task<PaymentAuthorizationResult> RecoverLinklyAsync(
        LocalCardPaymentAttempt attempt,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RecoverSquareAsync(
        LocalSquarePaymentAttempt attempt,
        PosSessionState session,
        CancellationToken cancellationToken = default);
}

public interface IVoucherTenderClient
{
    Task<PaymentAuthorizationResult> RedeemAsync(
        decimal amount,
        PosSessionState session,
        string? voucherCode,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> IssueRefundAsync(
        decimal amount,
        PosSessionState session,
        string orderReference,
        string idempotencyKey,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(
        PosSessionState session,
        string voucherCode,
        string reservationToken,
        CancellationToken cancellationToken = default);
}

public sealed record PaymentAuthorizationResult(
    bool Approved,
    string? Reference = null,
    string? Message = null,
    decimal? AuthorizedAmount = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? Processor = null,
    string? Environment = null,
    string? ConnectionMode = null,
    string? TxnType = null,
    string? SessionId = null,
    string? TxnRef = null,
    string? ResponseCode = null,
    string? ResponseText = null,
    string? StatusKey = null,
    string? RequestedConnectionMode = null,
    string? ActualConnectionMode = null,
    IReadOnlyList<string>? FallbackAttemptedModes = null,
    bool FallbackSucceeded = false,
    bool FallbackAllowed = false,
    bool ResultUnknown = false);

public sealed record CardPaymentOrderDraft(
    Guid OrderGuid,
    PosSessionState Session,
    PosCartSnapshot CartSnapshot,
    IReadOnlyList<PaymentTender> CurrentTenders,
    decimal ActualAmount,
    decimal CardAmount,
    string TxnType,
    string? OriginalReference,
    DateTimeOffset CreatedAt);

public sealed record PaymentTenderAttemptResult(
    bool Succeeded,
    string StatusKey,
    PaymentTender? Tender = null,
    string? StatusMessage = null,
    bool IsTerminalDecline = false,
    CardPaymentResultDisposition? CardResult = null,
    CardRecoveryAttemptKey? RecoveryAttemptKey = null,
    Guid? RecoveryOrderGuid = null)
{
    public static PaymentTenderAttemptResult Success(PaymentTender tender, string statusKey, string? statusMessage = null)
    {
        return new PaymentTenderAttemptResult(true, statusKey, tender, statusMessage);
    }

    public static PaymentTenderAttemptResult Fail(
        string statusKey,
        string? statusMessage = null,
        bool isTerminalDecline = false)
    {
        return new PaymentTenderAttemptResult(false, statusKey, null, statusMessage, isTerminalDecline);
    }
}

public sealed class UnavailableCardTerminalClient : ICardTerminalClient
{
    public static UnavailableCardTerminalClient Instance { get; } = new();

    private UnavailableCardTerminalClient()
    {
    }

    public Task<PaymentAuthorizationResult> AuthorizeAsync(
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentAuthorizationResult(false));
    }

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentAuthorizationResult(false));
    }
}

public sealed class UnavailableVoucherTenderClient : IVoucherTenderClient
{
    public static UnavailableVoucherTenderClient Instance { get; } = new();

    private UnavailableVoucherTenderClient()
    {
    }

    public Task<PaymentAuthorizationResult> RedeemAsync(
        decimal amount,
        PosSessionState session,
        string? voucherCode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentAuthorizationResult(false));
    }

    public Task<PaymentAuthorizationResult> IssueRefundAsync(
        decimal amount,
        PosSessionState session,
        string orderReference,
        string idempotencyKey,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentAuthorizationResult(false));
    }

    public Task<bool> ReleaseAsync(
        PosSessionState session,
        string voucherCode,
        string reservationToken,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}

public sealed record CashPaymentWorkflowResult(
    LocalOrder Order,
    decimal TenderedAmount,
    decimal ChangeAmount,
    int PendingSyncCount,
    PosSessionState UpdatedSession,
    bool HasPostCommitWarning = false);

public sealed class PaymentUploadFailedException : InvalidOperationException
{
    public PaymentUploadFailedException(
        Guid orderGuid,
        decimal tenderedAmount,
        decimal changeAmount,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        OrderGuid = orderGuid;
        TenderedAmount = tenderedAmount;
        ChangeAmount = changeAmount;
    }

    public Guid OrderGuid { get; }

    public decimal TenderedAmount { get; }

    public decimal ChangeAmount { get; }
}

public sealed class CardPaymentPersistenceUnknownException : InvalidOperationException
{
    public CardPaymentPersistenceUnknownException(
        Guid orderGuid,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        OrderGuid = orderGuid;
    }

    public Guid OrderGuid { get; }
}
