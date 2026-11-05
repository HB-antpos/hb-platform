using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Services;

/// <summary>
/// 分期续付刷卡入账后的旁路核对（软记）。与普通销售订单同口径：刷卡结果由收银机上报，
/// 钱已经扣了，核对不通过也不回滚入账，只把异常登记进 POSM_CardTenderReconciliationIssue 并告警。
/// </summary>
public interface IInstallmentRepaymentCardTenderVerifier
{
    Task VerifyAsync(InstallmentRepaymentClaimRecord claim, InstallmentRepaymentClaimCommitRequest request);

    /// <summary>主管结案留痕：登记一条 Warning 级异常（含主管、结论、原因、参考号），供财务按银行流水复核。</summary>
    Task RecordSupervisorResolutionAsync(
        InstallmentRepaymentClaimRecord claim,
        InstallmentRepaymentSupervisorDecision decision,
        string reason,
        string? reference,
        InstallmentRepaymentClaimIdentity supervisor) => Task.CompletedTask;
}

public sealed class InstallmentRepaymentCardTenderVerifier(
    ICardTenderReconciliationRepository repository,
    ILogger<InstallmentRepaymentCardTenderVerifier>? logger = null) : IInstallmentRepaymentCardTenderVerifier
{
    // 入账已提交才核对，不跟随请求取消；超时只记日志，对账作业兜底。
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(15);

    public async Task VerifyAsync(InstallmentRepaymentClaimRecord claim, InstallmentRepaymentClaimCommitRequest request)
    {
        using var timeout = new CancellationTokenSource(VerificationTimeout);
        try
        {
            var payment = new CardTenderPaymentContext(
                CardTenderIssueSources.InstallmentRepayment,
                claim.StoreCode,
                claim.ClaimantDeviceCode,
                claim.InstallmentGuid.ToString("D"),
                claim.PaymentGuid,
                claim.Amount,
                request.Reference,
                request.CardTransactions);
            var issues = new List<CardTenderIssue>(CardTenderEvidenceChecker.CheckPaymentConsistency(payment));
            issues.AddRange(await FindDuplicateTxnRefsAsync(payment, timeout.Token));
            if (LinklyBackendPaymentReference.TryGetPrintMarker(request.Reference, out var environment, out var sessionId))
            {
                var sessionVerifier = new CardTenderOrderVerifier(repository);
                issues.AddRange(await sessionVerifier.VerifySessionAsync(payment, environment, sessionId, timeout.Token));
            }

            if (issues.Count == 0)
            {
                return;
            }

            foreach (var issue in issues)
            {
                logger?.LogWarning(
                    "CardTenderReconciliation installment repayment issue type={IssueType} severity={Severity} installmentGuid={InstallmentGuid} paymentGuid={PaymentGuid} store={StoreCode} device={DeviceCode} provider={Provider} sessionId={SessionId} detail={Detail}",
                    issue.IssueType,
                    issue.Severity,
                    issue.OrderGuid,
                    issue.PaymentGuid,
                    issue.StoreCode,
                    issue.DeviceCode,
                    claim.Provider,
                    issue.SessionId,
                    issue.Detail);
            }

            await repository.UpsertIssuesAsync(issues, timeout.Token);
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "CardTenderReconciliation installment repayment verify failed installmentGuid={InstallmentGuid} paymentGuid={PaymentGuid} store={StoreCode} device={DeviceCode}",
                claim.InstallmentGuid,
                claim.PaymentGuid,
                claim.StoreCode,
                claim.ClaimantDeviceCode);
        }
    }

    public async Task RecordSupervisorResolutionAsync(
        InstallmentRepaymentClaimRecord claim,
        InstallmentRepaymentSupervisorDecision decision,
        string reason,
        string? reference,
        InstallmentRepaymentClaimIdentity supervisor)
    {
        using var timeout = new CancellationTokenSource(VerificationTimeout);
        var issueType = decision == InstallmentRepaymentSupervisorDecision.ConfirmedCharged
            ? CardTenderIssueTypes.SupervisorConfirmedCharged
            : CardTenderIssueTypes.SupervisorConfirmedNotCharged;
        var issue = new CardTenderIssue(
            issueType,
            CardTenderIssueSeverities.Warning,
            CardTenderIssueSources.InstallmentRepayment,
            claim.StoreCode,
            supervisor.DeviceCode,
            null,
            null,
            reference,
            claim.InstallmentGuid.ToString("D"),
            claim.PaymentGuid.ToString("D"),
            claim.Amount,
            CardTenderEvidenceChecker.Truncate(
                $"Supervisor {supervisor.CashierName} ({supervisor.CashierId}) on device {supervisor.DeviceCode} resolved the {claim.Provider} card repayment " +
                $"(operation {claim.OperationGuid:D}, started on {claim.ClaimantDeviceCode} by {claim.CashierName}) as {decision}. Reason: {reason}" +
                (reference is null ? string.Empty : $" Reference: {reference}")));
        logger?.LogWarning(
            "Installment card repayment resolved by supervisor decision={Decision} installmentGuid={InstallmentGuid} operationGuid={OperationGuid} paymentGuid={PaymentGuid} amount={Amount} provider={Provider} supervisor={SupervisorId} device={DeviceCode} reason={Reason} reference={Reference}",
            decision,
            claim.InstallmentGuid,
            claim.OperationGuid,
            claim.PaymentGuid,
            claim.Amount,
            claim.Provider,
            supervisor.CashierId,
            supervisor.DeviceCode,
            reason,
            reference);
        try
        {
            await repository.UpsertIssuesAsync([issue], timeout.Token);
        }
        catch (Exception ex)
        {
            // 结案本身已经落库；留痕失败只记日志（日志里已有完整结论）。
            logger?.LogError(ex, "Supervisor resolution audit issue could not be recorded operationGuid={OperationGuid}", claim.OperationGuid);
        }
    }

    private async Task<IReadOnlyList<CardTenderIssue>> FindDuplicateTxnRefsAsync(
        CardTenderPaymentContext payment,
        CancellationToken cancellationToken)
    {
        var issues = new List<CardTenderIssue>();
        var txnRefs = (payment.CardTransactions ?? [])
            .Select(transaction => transaction.TxnRef?.Trim())
            .Where(txnRef => !string.IsNullOrEmpty(txnRef))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>();
        foreach (var txnRef in txnRefs)
        {
            var others = await repository.FindOtherPaymentsWithCardTxnRefAsync(
                txnRef,
                payment.PaymentGuid,
                cancellationToken);
            if (others.Count == 0)
            {
                continue;
            }

            issues.Add(CardTenderEvidenceChecker.Create(
                payment,
                CardTenderIssueTypes.DuplicateCardTxnRef,
                CardTenderIssueSeverities.Error,
                $"Card txnRef={txnRef} is already recorded on other payments ({string.Join(",", others)}); the same card approval may have been recorded twice.",
                txnRef));
        }

        return issues;
    }
}
