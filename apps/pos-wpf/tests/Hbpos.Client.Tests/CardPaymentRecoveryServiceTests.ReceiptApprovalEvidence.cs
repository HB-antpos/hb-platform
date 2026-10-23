using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

// 2026-10-22 1002 店事故回归：本地 Linkly 交易已打出批准小票（证据已落库），GetLast 却回 JH / NOT FOUND。
// 恢复必须凭证据建单；主管实时核验必须拦住“确认未付款”；确认已付款不再强制填支付参考号。
public sealed partial class CardPaymentRecoveryServiceTests
{
    private const string ReceiptEvidenceTxnRef = LinklyLocalReceiptApprovalEvidenceTests.TxnRef;

    private static LocalCardPaymentAttempt CreateLocalIpAttemptWithReceiptEvidence(decimal? evidenceAmount = null)
    {
        var attempt = CreateAttempt(
            sessionId: null,
            txnRef: ReceiptEvidenceTxnRef,
            status: LocalCardPaymentAttemptStatus.Recovering,
            connectionMode: LinklyConnectionMode.LocalIp);
        var amount = evidenceAmount ?? attempt.Amount;
        Assert.True(LinklyLocalReceiptApprovalEvidence.TryParse(
            [LinklyLocalReceiptApprovalEvidenceTests.ApprovedReceipt(amount: $"${amount:0.00}")],
            ReceiptEvidenceTxnRef,
            amount,
            out var evidence));
        return attempt with { ReceiptApprovalEvidence = evidence!.Serialize() };
    }

    // EFT-Client 正常回答但不是这笔（JH / NOT FOUND、空 TxnRef）时 LinklyTerminalClient 的真实返回形态。
    private static PaymentAuthorizationResult LocalNotFoundResult() =>
        new(
            false,
            null,
            "ANZ Linkly recovery could not confirm the previous transaction.",
            TxnType: "P",
            StatusKey: LinklyLocalTxnRef.LastTransactionNotMatchedStatusKey,
            ResultUnknown: true);

    [Fact]
    public async Task RecoverLatestAsync_local_ip_not_found_with_receipt_evidence_completes_order()
    {
        var attempt = CreateLocalIpAttemptWithReceiptEvidence();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeLinklyTerminalClient(LocalNotFoundResult());
        var service = CreateService(
            attempts,
            orders,
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(1, terminal.RecoverCallCount);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal($"ANZ:{ReceiptEvidenceTxnRef}", attempts.PaymentReference);
        Assert.Equal($"ANZ:{ReceiptEvidenceTxnRef}", result.Order!.Payments.Single().Reference);
    }

    [Fact]
    public async Task RecoverLatestAsync_local_ip_not_found_with_evidence_for_another_amount_stays_unknown()
    {
        // 证据金额与 attempt 不一致（串单或篡改）：不能据此建单。
        var attempt = CreateLocalIpAttemptWithReceiptEvidence(evidenceAmount: 13.99m);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(
            attempts,
            orders,
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            new FakeLinklyTerminalClient(LocalNotFoundResult()));

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, orders.SaveCount);
    }

    [Fact]
    public async Task RecoverLatestAsync_local_ip_terminal_decline_for_this_txn_overrides_receipt_evidence()
    {
        // 终端明确回答了这笔（引用、金额都对得上的拒绝）时以终端为准，小票证据不覆盖。
        var attempt = CreateLocalIpAttemptWithReceiptEvidence();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(
            attempts,
            orders,
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            new FakeLinklyTerminalClient(LocalTerminalResult(ReceiptEvidenceTxnRef, false, "05", "DECLINED")));

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.NotEqual(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(0, orders.SaveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvePaymentAsync_local_ip_receipt_evidence_rejects_confirm_not_paid(bool terminalOffline)
    {
        var attempt = CreateLocalIpAttemptWithReceiptEvidence();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var terminal = new FakeLinklyTerminalClient(terminalOffline
            ? LocalTerminalResult(ReceiptEvidenceTxnRef, false, "PF", "PINpad Offline", resultUnknown: true)
            : LocalNotFoundResult());
        var service = CreateService(
            attempts,
            new FakeLocalOrderRepository(),
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            terminal);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.False(result.ResolutionPersisted);
        Assert.Contains("approved", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(attempts.LastPaymentJournal);
    }

    [Fact]
    public async Task ResolvePaymentAsync_local_ip_confirm_paid_without_reference_or_evidence_is_accepted()
    {
        // 门店反馈参考号太长、找不到：确认已付款只需主管授权。
        var attempt = CreateLocalIpPendingAttempt() with
        {
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(
            attempts,
            orders,
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            new FakeLinklyTerminalClient(LocalNotFoundResult()));

        var result = await service.ResolvePaymentAsync(
            new CardPaymentSupervisorResolution(
                attempt.AttemptGuid,
                CardProcessorKind.Linkly,
                CardPaymentSupervisorDecision.ConfirmPaid,
                string.Empty,
                "MANAGER-01"),
            new PosCartService(),
            Session);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.ResolutionPersisted);
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.RecoveryResult?.Outcome);
        Assert.Equal(1, orders.SaveCount);
        var journal = Assert.IsType<LocalFinancialSupervisorResolution>(attempts.LastPaymentJournal);
        Assert.Null(journal.Evidence);
        Assert.Null(journal.FinancialReference);
    }

    [Fact]
    public void Queue_item_exposes_receipt_reference_only_for_matching_evidence()
    {
        var withEvidence = CreateLocalIpAttemptWithReceiptEvidence();
        var mismatched = CreateLocalIpAttemptWithReceiptEvidence(evidenceAmount: 13.99m);

        Assert.Equal(
            "RRN P29R5YNFN4XM / APPROVAL CODE 267836",
            CardPaymentRecoveryService.TryReadReceiptApprovalEvidence(withEvidence)?.DisplayReference);
        Assert.Null(CardPaymentRecoveryService.TryReadReceiptApprovalEvidence(mismatched));
        Assert.Null(CardPaymentRecoveryService.TryReadReceiptApprovalEvidence(
            withEvidence with { ConnectionMode = LinklyConnectionMode.CloudBackendAsync.ToString() }));
    }
}
