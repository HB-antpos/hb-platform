using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 收款流程里的 Zeller 部分：付款页刷卡（扣款、按原交易退款）、订单落盘前沿用草稿里的订单 GUID、落盘后标记尝试已建单。
/// </summary>
/// <remarks>
/// 中文注释：Zeller 的客户端（ZellerCardTerminalClient）自己负责先落库、调用刷卡机、核实和结果未知，
/// 所以这里不碰 Linkly/Square 那套尝试变量、提交令牌和 CAS，只做三件事：
/// 1. 用和 Square 相同的 CardPaymentOrderDraft 做订单草稿，交给客户端随尝试一起落库；
/// 2. 把客户端的结论转成付款页认识的结果——批准就加付款行（幂等键 ZELLER_ATTEMPT:{guid}），
///    结果未知就带上恢复身份锁住付款页；
/// 3. 订单落盘时沿用草稿里的订单 GUID，落盘后把尝试标记为已建单。
/// 判断「请求是否已经交给刷卡机」只看客户端报告的 Submitted 进度：在那之前抛出的异常（参数、落库）一定没扣款。
/// </remarks>
public sealed partial class CashPaymentWorkflowService
{
    internal const string ZellerAttemptTenderKeyPrefix = "ZELLER_ATTEMPT:";

    /// <summary>Zeller 单笔扣款的最低金额：100 分（1.00 澳元）。官方限制，不能在刷卡机上调整。</summary>
    internal const int ZellerMinimumPurchaseCents = 100;

    private async Task<PaymentTenderAttemptResult> AuthorizeZellerTenderAsync(
        CardTerminalSettings settings,
        decimal amount,
        decimal remainingAmount,
        PosSessionState session,
        decimal actualAmount,
        IReadOnlyList<PaymentTender> tenderSnapshot,
        PosCartSnapshot? cartSnapshot,
        string? referenceText,
        bool isRefund,
        string declinedStatusKey,
        string approvedStatusKey,
        CancellationToken cancellationToken)
    {
        var operation = isRefund ? "refund" : "sale";
        if (_zellerCardTerminalClient is null)
        {
            ConsoleLog.Write("Zeller", $"workflow {operation} blocked reason=client-unavailable");
            return CreateCardFailureResult(ZellerStatusKey("sdkUnavailable"));
        }

        if (!TryConvertToZellerCents(amount, out var amountCents))
        {
            return CreateCardFailureResult(ZellerStatusKey("invalidRequest"));
        }

        if (!isRefund && amountCents < ZellerMinimumPurchaseCents)
        {
            // 中文注释：Zeller 官方限制单笔扣款最低 1.00 澳元，刷卡机上改不了。2026-11-05 真机实测：99 分在约 5 秒内
            // 被回绝并返回 Cancelled(12)；程序用无人值守模式（不弹 SDK 窗口），收银员看不到任何原因，只会以为被人取消。
            // 所以请求发出前就拦下并说明原因。拆分付款时这里的 amount 是刷卡那一部分，同样适用。退款不受此限制。
            ConsoleLog.Write("Zeller", $"workflow sale blocked reason=below-minimum amountCents={amountCents} minimumCents={ZellerMinimumPurchaseCents}");
            return CreateCardFailureResult(ZellerStatusKey("minimumAmount"));
        }

        if (cartSnapshot is null ||
            string.IsNullOrWhiteSpace(session.StoreCode) ||
            string.IsNullOrWhiteSpace(session.DeviceCode) ||
            string.IsNullOrWhiteSpace(session.CashierId))
        {
            // 没有订单草稿就无法在结果未知或程序退出后补单，不能发起扣款。
            ConsoleLog.Write("Zeller", $"workflow {operation} blocked reason=missing-order-context");
            return CreateCardFailureResult(ZellerStatusKey("invalidRequest"));
        }

        // 中文注释：无关联退款（默认关闭）。原付款无法关联到 Zeller 交易时（目前付款页只对主管确认的未核实付款提供：
        // 只有 reference、没有交易号），Zeller 没法按原交易退。开关关闭：交给收银员改用其他方式；开关打开：改走刷卡退款，
        // 由顾客在刷卡机上刷一张卡，钱退到刷的那张卡，不一定是原卡。能关联到原交易的退款永远按原交易退，不看这个开关。
        var unmatchedRefund = isRefund && !ZellerPaymentReference.TryGetOriginalTransactionUuid(referenceText, out _);
        if (unmatchedRefund && !settings.ZellerAllowUnmatchedRefund)
        {
            ConsoleLog.Write("Zeller", $"workflow refund blocked reason=original-not-verified originalReference={LogValue(referenceText)}");
            return CreateCardFailureResult(ZellerStatusKey("originalUnverified"));
        }

        if (unmatchedRefund)
        {
            ConsoleLog.WriteWarning("Zeller", $"workflow refund proceeding as UNMATCHED card-present refund originalReference={LogValue(referenceText)} amountCents={amountCents}");
        }

        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            session,
            cartSnapshot,
            tenderSnapshot.ToArray(),
            actualAmount,
            amount,
            isRefund ? "R" : "P",
            referenceText,
            DateTimeOffset.UtcNow);
        var draftJson = JsonSerializer.Serialize(draft, CardAttemptJsonOptions);
        var progress = new ZellerWorkflowProgress();

        ZellerPaymentOutcome outcome;
        try
        {
            outcome = isRefund
                ? await _zellerCardTerminalClient.RefundAsync(
                    new ZellerRefundContext(
                        amountCents,
                        referenceText!,
                        draftJson,
                        session.StoreCode,
                        session.DeviceCode,
                        session.CashierId,
                        RefundBusinessKey: BuildRefundBusinessKey(
                            CardProcessorKind.Zeller,
                            settings.Environment.ToString(),
                            session,
                            amount,
                            referenceText,
                            "AUD"),
                        Unmatched: unmatchedRefund),
                    progress,
                    cancellationToken)
                : await _zellerCardTerminalClient.PurchaseAsync(
                    new ZellerPurchaseContext(amountCents, draftJson, session.StoreCode, session.DeviceCode, session.CashierId),
                    progress,
                    cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (progress.SubmittedAttemptGuid is not { } submittedAttemptGuid)
            {
                // 请求还没交给刷卡机（参数校验或尝试落库失败）：确定没有扣款，收银员可以重试或改用其他方式。
                ConsoleLog.WriteWarning(
                    "Zeller",
                    $"workflow {operation} not started error={ex.GetType().Name} message={LogValue(ex.Message)}",
                    exception: ex);
                return CreateCardFailureResult(ZellerStatusKey("notStarted"));
            }

            // 请求已经发出：任何异常都不能当成失败让顾客重新刷卡。
            LogCardTerminalCallException(submittedAttemptGuid, isRefund, ex);
            return CreateZellerResultUnknown(submittedAttemptGuid, draft);
        }

        var authorization = ZellerAuthorizationMapper.ToAuthorizationResult(outcome);
        ConsoleLog.Write(
            "Zeller",
            $"workflow {operation} finished attempt={outcome.AttemptGuid:D} state={outcome.State} failure={outcome.FailureKind} unknown={outcome.UnknownReason}");

        switch (outcome.State)
        {
            case ZellerPaymentState.Approved:
                return CreateZellerApprovedResult(outcome, authorization, draft, amount, remainingAmount, referenceText, isRefund, approvedStatusKey);

            case ZellerPaymentState.Unknown when outcome.UnknownReason == ZellerUnknownReason.RefundAlreadyInProgress:
                // 这一笔退货已有退款在处理（那条记录不是这次发起的），不锁付款页，只提示去恢复中心看那一笔。
                return CreateCardFailureResult(authorization.StatusKey!);

            case ZellerPaymentState.Unknown:
                return CreateZellerResultUnknown(outcome.AttemptGuid, draft);

            default:
                return CreateCardFailureResult(
                    authorization.StatusKey ?? declinedStatusKey,
                    // 被拒时带上银行的响应文案（付款页的「银行未批准」浮层会显示它）；其余用本地化文案。
                    outcome.State == ZellerPaymentState.Declined ? authorization.Message : null,
                    isTerminalDecline: outcome.State == ZellerPaymentState.Declined);
        }
    }

    private PaymentTenderAttemptResult CreateZellerApprovedResult(
        ZellerPaymentOutcome outcome,
        PaymentAuthorizationResult authorization,
        CardPaymentOrderDraft draft,
        decimal amount,
        decimal remainingAmount,
        string? referenceText,
        bool isRefund,
        string approvedStatusKey)
    {
        var authorizedAmount = authorization.AuthorizedAmount ?? 0m;
        if (authorizedAmount != amount || authorizedAmount > remainingAmount || string.IsNullOrWhiteSpace(authorization.Reference))
        {
            // 分类器已经核对过金额和 reference，走到这里说明数据异常。钱可能已经扣了，按结果未知锁住，交给恢复中心。
            TryWriteCardRecoveryLog(
                $"zeller approved result rejected attemptGuid={outcome.AttemptGuid:D} requested={amount:0.00} authorized={authorizedAmount:0.00} remaining={remainingAmount:0.00} -> ResultUnknown");
            return CreateZellerResultUnknown(outcome.AttemptGuid, draft);
        }

        var reference = isRefund
            ? CardRefundReference.Format(authorization.Reference, referenceText!)
            : authorization.Reference;
        return PaymentTenderAttemptResult.Success(
            new PaymentTender(
                PaymentMethodKind.Card,
                isRefund ? -authorizedAmount : authorizedAmount,
                reference,
                CardTransactions: authorization.CardTransactions,
                IdempotencyKey: FormatZellerAttemptTenderKey(outcome.AttemptGuid)),
            approvedStatusKey);
    }

    /// <summary>结果未知：锁住付款页，并带上恢复身份（处理器 + 尝试编号 + 草稿里的订单 GUID），恢复中心据此补单或结案。</summary>
    private PaymentTenderAttemptResult CreateZellerResultUnknown(Guid attemptGuid, CardPaymentOrderDraft draft)
    {
        var result = _cardPaymentResultPolicyResolver.Apply(
            PaymentTenderAttemptResult.Fail(ZellerStatusKey("resultUnknown")));
        return result with
        {
            RecoveryAttemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Zeller, attemptGuid),
            RecoveryOrderGuid = draft.OrderGuid
        };
    }

    /// <summary>订单落盘前：付款里有 Zeller 尝试时，沿用那次刷卡前草稿里的订单 GUID。没有 Zeller 付款返回 null。</summary>
    private async Task<RecoverableOrderPersistence?> TryPrepareZellerOrderPersistenceAsync(
        LocalOrder order,
        IReadOnlyList<PaymentTender> tenders,
        CancellationToken cancellationToken)
    {
        var attemptGuid = ReadZellerAttemptGuids(tenders).FirstOrDefault();
        if (attemptGuid == Guid.Empty || _zellerPaymentAttemptRepository is null)
        {
            return null;
        }

        var attempt = await RunLocalStoreAsync(
            () => _zellerPaymentAttemptRepository.GetAsync(attemptGuid, cancellationToken),
            cancellationToken);
        if (attempt is null)
        {
            return null;
        }

        var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, CardAttemptJsonOptions);
        return new RecoverableOrderPersistence(
            draft is null || draft.OrderGuid == Guid.Empty ? order : order with { OrderGuid = draft.OrderGuid },
            AlreadyPersisted: false);
    }

    /// <summary>订单落盘后：把付款里的 Zeller 尝试标记为已建单。没有 Zeller 付款时直接返回，不读库。</summary>
    private async Task MarkCompletedZellerAttemptsAsync(
        IReadOnlyList<PaymentTender> tenders,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var attemptGuids = ReadZellerAttemptGuids(tenders).ToArray();
        if (attemptGuids.Length == 0)
        {
            return;
        }

        if (_zellerCardTerminalClient is null)
        {
            throw new InvalidOperationException("The order has Zeller payments but the Zeller client is not available.");
        }

        foreach (var attemptGuid in attemptGuids)
        {
            // 订单已经落盘，标记不随付款页取消而中断。
            var marked = await _zellerCardTerminalClient.MarkOrderCompletedAsync(attemptGuid, CancellationToken.None);
            if (marked)
            {
                ConsoleLog.Write("Zeller", $"attempt {attemptGuid:D} marked order-completed order={orderGuid:D}");
            }
            else
            {
                // 重试落单时可能已经标记过；其余情况（记录不是批准状态）留给恢复中心核对。
                ConsoleLog.WriteWarning(
                    "Zeller",
                    $"attempt {attemptGuid:D} was not marked order-completed (not in Approved state) order={orderGuid:D}");
            }
        }
    }

    internal static string FormatZellerAttemptTenderKey(Guid attemptGuid) => $"{ZellerAttemptTenderKeyPrefix}{attemptGuid:N}";

    internal static bool TryReadZellerAttemptTenderKey(string? value, out Guid attemptGuid)
    {
        attemptGuid = Guid.Empty;
        return !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith(ZellerAttemptTenderKeyPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(value[ZellerAttemptTenderKeyPrefix.Length..], "N", out attemptGuid);
    }

    private static IEnumerable<Guid> ReadZellerAttemptGuids(IEnumerable<PaymentTender> tenders) =>
        tenders
            .Select(tender => TryReadZellerAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid) ? attemptGuid : Guid.Empty)
            .Where(attemptGuid => attemptGuid != Guid.Empty)
            .Distinct();

    private static string ZellerStatusKey(string suffix) => ZellerCardPaymentResultPolicy.StatusKeyPrefix + suffix;

    /// <summary>Zeller 金额按分传；只接受正的整分金额（收银端金额本来就保留两位小数）。</summary>
    internal static bool TryConvertToZellerCents(decimal amount, out int amountCents)
    {
        amountCents = 0;
        var cents = amount * 100m;
        if (amount <= 0m || cents != decimal.Truncate(cents) || cents > int.MaxValue)
        {
            return false;
        }

        amountCents = (int)cents;
        return true;
    }

    /// <summary>
    /// 接收 Zeller 客户端的进度：记下请求是否已经交给刷卡机（以及是哪条尝试），核实中时提示付款页。
    /// 同步回调，不经过界面线程；付款页的提示经 CardTerminalProgressScope 投递。
    /// </summary>
    private sealed class ZellerWorkflowProgress : IProgress<ZellerPaymentProgress>
    {
        private readonly object _gate = new();
        private Guid? _submittedAttemptGuid;

        public Guid? SubmittedAttemptGuid
        {
            get
            {
                lock (_gate)
                {
                    return _submittedAttemptGuid;
                }
            }
        }

        public void Report(ZellerPaymentProgress value)
        {
            if (value.Kind == ZellerPaymentProgressKind.Submitted && value.AttemptGuid != Guid.Empty)
            {
                lock (_gate)
                {
                    _submittedAttemptGuid = value.AttemptGuid;
                }
            }
            else if (value.Kind == ZellerPaymentProgressKind.Verifying)
            {
                CardTerminalProgressScope.Report(new CardTerminalProgress(CardTerminalProgressKind.ResultVerifying, value.VerificationRound));
            }
        }
    }
}
