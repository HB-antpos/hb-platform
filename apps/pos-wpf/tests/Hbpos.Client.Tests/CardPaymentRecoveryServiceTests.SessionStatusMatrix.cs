using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

// Linkly 后端会话「已批准 / 已结束 / 批准金额核验」状态矩阵（特征化测试）之一：异常中心恢复服务这一处。
// 另外三处的矩阵见 LinklyBackendSessionStatusMatrixTests.cs 与 CashPaymentWorkflowServiceTask2BTests.SessionStatusMatrix.cs。
// 这里钉住的是恢复服务"当前"对每种状态值的处理：包括它与实时收款口径不同的地方（例如 Completed 缺成败位只会得到 Unknown，
// Pending + 已核验批准通知也只是 Checking），重构为共用分类器前后必须逐格一致。
public sealed partial class CardPaymentRecoveryServiceTests
{
    private const string MatrixSessionId = "ACTIVE-MATRIX";
    private const string MatrixTxnRef = "TXN-MATRIX";

    private static IReadOnlyList<LinklyCloudBackendNotificationDto> MatrixEvidence(string evidence, long amountMinor = 1000) =>
        evidence switch
        {
            "none" => [],
            "approved" =>
            [
                new LinklyCloudBackendNotificationDto(
                    "transaction",
                    $$"""{ "Response": { "Success": true, "TxnRef": "{{MatrixTxnRef}}", "ResponseCode": "00", "ResponseText": "APPROVED", "AmtPurchase": {{amountMinor}}, "TxnType": "P" } }""",
                    DateTimeOffset.Parse("2026-06-05T10:01:00+10:00"))
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null)
        };

    // ------------------------------------------------------------------
    // 付款页 / 异常中心的活动会话恢复（RecoverActiveSessionAsync）：状态 × 结果字段 → 出口
    // ------------------------------------------------------------------

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    // 已批准：Completed + TransactionSuccess=true（唯一的批准口径，状态名大小写不敏感）
    [InlineData("Completed", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.ActiveSessionApproved, 0, 1)]
    [InlineData("completed", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.ActiveSessionApproved, 0, 1)]
    [InlineData("COMPLETED", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.ActiveSessionApproved, 0, 1)]
    [InlineData("Completed", true, "05", "DECLINED", "none", CardPaymentRecoveryOutcome.ActiveSessionApproved, 0, 1)] // 成败位为准
    // Completed + TransactionSuccess=false：明确未付款
    [InlineData("Completed", false, "05", "DECLINED", "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("Completed", false, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)] // 明确失败压过批准码
    // Completed 缺成败位：先按 SessionId 再查一次权威状态；仍缺则无结论，绝不凭批准码/通知自动认作批准
    [InlineData("Completed", null, null, null, "none", CardPaymentRecoveryOutcome.Unknown, 0, 0)]
    [InlineData("Completed", null, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Unknown, 0, 0)]
    [InlineData("Completed", null, null, null, "approved", CardPaymentRecoveryOutcome.Unknown, 0, 0)]
    // 失败类状态名：明确未付款（含两种取消拼写、与大小写）
    [InlineData("Failed", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("Failed", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("failed", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("NotSubmitted", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("Cancelled", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("Canceled", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("cancelled", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    // 非终态：先 ResumeSessionUntilFinal，仍非终态则 Checking（Pending + 批准通知在这里不算终态）
    [InlineData("Pending", null, null, null, "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("Pending", null, null, null, "approved", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("Pending", true, "00", "APPROVED", "approved", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("TokenRefreshRequired", null, null, null, "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("SupervisorResolved", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("Weird", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    // 状态名不 Trim：带空白的 Completed 当成未知状态
    [InlineData(" Completed", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("Completed ", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    public async Task RecoverActiveSessionAsync_status_matrix(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string? responseText,
        string evidence,
        CardPaymentRecoveryOutcome expectedOutcome,
        int expectedResumeCalls,
        int expectedAcknowledgements)
    {
        var session = CreateStatus(
            status,
            MatrixSessionId,
            MatrixTxnRef,
            responseCode,
            responseText,
            transactionSuccess,
            MatrixEvidence(evidence));
        var attempts = new FakeCardPaymentAttemptRepository(null)
        {
            RecentAttempts = [CreateCompletedSaleFor(MatrixSessionId, MatrixTxnRef)]
        };
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient { ResumableStatus = session, Status = session };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverActiveSessionAsync(new PosCartService(), Session);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedResumeCalls, backend.ResumeCallCount);
        Assert.Equal(expectedAcknowledgements, backend.AcknowledgeCallCount);
        Assert.Equal(0, orders.SaveCount);
    }

    // ------------------------------------------------------------------
    // 定点恢复一条 ActiveSession attempt（RecoverAttemptAsync → RecoverActiveSessionAttemptAsync）：
    // 与上面同一组判定，但是另一个入口，单独钉住
    // ------------------------------------------------------------------

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    [InlineData("Completed", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.ActiveSessionApproved, 0, 1)]
    [InlineData("completed", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.ActiveSessionApproved, 0, 1)]
    [InlineData("Completed", false, "05", "DECLINED", "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("Completed", null, null, null, "none", CardPaymentRecoveryOutcome.Unknown, 0, 0)]
    [InlineData("Completed", null, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Unknown, 0, 0)]
    [InlineData("Completed", null, null, null, "approved", CardPaymentRecoveryOutcome.Unknown, 0, 0)]
    [InlineData("Failed", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("NotSubmitted", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("Cancelled", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("Canceled", null, null, null, "none", CardPaymentRecoveryOutcome.ActiveSessionNotPaid, 0, 1)]
    [InlineData("Pending", null, null, null, "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("Pending", null, null, null, "approved", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("TokenRefreshRequired", null, null, null, "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("SupervisorResolved", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData("Weird", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    [InlineData(" Completed", true, "00", "APPROVED", "none", CardPaymentRecoveryOutcome.Checking, 1, 0)]
    public async Task RecoverAttemptAsync_targeted_active_session_status_matrix(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string? responseText,
        string evidence,
        CardPaymentRecoveryOutcome expectedOutcome,
        int expectedResumeCalls,
        int expectedAcknowledgements)
    {
        var attempt = CreateAttempt(MatrixSessionId, MatrixTxnRef, LocalCardPaymentAttemptStatus.Recovering) with
        {
            OperationKind = "ActiveSession",
            OperationGuid = Guid.NewGuid(),
            OrderDraftJson = "{}",
            Amount = 0m
        };
        var session = CreateStatus(
            status,
            MatrixSessionId,
            MatrixTxnRef,
            responseCode,
            responseText,
            transactionSuccess,
            MatrixEvidence(evidence));
        var attempts = new FakeCardPaymentAttemptRepository(attempt)
        {
            RecentAttempts = [CreateCompletedSaleFor(MatrixSessionId, MatrixTxnRef)]
        };
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient { Status = session };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedResumeCalls, backend.ResumeCallCount);
        Assert.Equal(expectedAcknowledgements, backend.AcknowledgeCallCount);
        Assert.Equal(0, orders.SaveCount);
    }

    // ------------------------------------------------------------------
    // 失败归类（MapFailureStatus）：只看 "Status ResponseText" 的文本，响应码不参与
    // ------------------------------------------------------------------

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    [InlineData("Failed", null, null, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("NotSubmitted", null, null, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Cancelled", null, null, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Canceled", null, null, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Failed", "Transaction timeout", null, LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("Failed", "TIMEOUT", null, LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("Failed", "Operator cancelled", null, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Failed", "CARD DECLINED", null, LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("Failed", "declined by issuer", null, LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("Failed", "timeout then cancelled", null, LocalCardPaymentAttemptStatus.TimedOut)]   // TIMEOUT 优先于 CANCEL
    [InlineData("Failed", "cancel or declined", null, LocalCardPaymentAttemptStatus.Cancelled)]      // CANCEL 优先于 DECLIN
    [InlineData("Failed", null, "TO", LocalCardPaymentAttemptStatus.Failed)]                          // 响应码不参与
    [InlineData("Completed", "DECLINED", "05", LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("Completed", "SYSTEM ERROR", "50", LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Completed", "TIMEOUT", "TO", LocalCardPaymentAttemptStatus.TimedOut)]
    public async Task RecoverActiveSessionAsync_failure_text_maps_to_the_persisted_attempt_status(
        string status,
        string? responseText,
        string? responseCode,
        LocalCardPaymentAttemptStatus expectedStatus)
    {
        var session = CreateStatus(
            status,
            MatrixSessionId,
            MatrixTxnRef,
            responseCode,
            responseText,
            transactionSuccess: string.Equals(status, "Completed", StringComparison.Ordinal) ? false : null);
        var attempts = new FakeCardPaymentAttemptRepository(null)
        {
            RecentAttempts = [CreateCompletedSaleFor(MatrixSessionId, MatrixTxnRef)]
        };
        var backend = new FakeLinklyBackendTerminalClient { ResumableStatus = session, Status = session };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.RecoverActiveSessionAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.ActiveSessionNotPaid, result.Outcome);
        Assert.Equal(expectedStatus, attempts.Status);
    }

    // ------------------------------------------------------------------
    // 定点恢复某条 attempt（RecoverAttemptAsync）：状态出口 + 已批准的金额核验
    // ------------------------------------------------------------------

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    [InlineData("Completed", true, "00", "APPROVED", CardPaymentRecoveryOutcome.OrderCompleted, 1)]
    [InlineData("completed", true, "00", "APPROVED", CardPaymentRecoveryOutcome.OrderCompleted, 1)]
    [InlineData("Completed", null, "00", "APPROVED", CardPaymentRecoveryOutcome.Unknown, 0)]   // 缺成败位：不凭批准码建单
    [InlineData("Completed", null, null, null, CardPaymentRecoveryOutcome.Unknown, 0)]
    [InlineData("Pending", null, null, null, CardPaymentRecoveryOutcome.Checking, 0)]
    [InlineData("TokenRefreshRequired", null, null, null, CardPaymentRecoveryOutcome.Checking, 0)]
    [InlineData("SupervisorResolved", true, "00", "APPROVED", CardPaymentRecoveryOutcome.Checking, 0)]
    [InlineData("Weird", true, "00", "APPROVED", CardPaymentRecoveryOutcome.Checking, 0)]
    [InlineData(" Completed", true, "00", "APPROVED", CardPaymentRecoveryOutcome.Checking, 0)]
    public async Task RecoverAttemptAsync_status_matrix_with_verified_amount(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string? responseText,
        CardPaymentRecoveryOutcome expectedOutcome,
        int expectedOrdersSaved)
    {
        var attempt = CreateAttempt("SESSION-MATRIX", "TXN-MATRIX-ATTEMPT");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = WithVerifiedCardTransaction(
                CreateStatus(status, "SESSION-MATRIX", "TXN-MATRIX-ATTEMPT", responseCode, responseText, transactionSuccess),
                10m)
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedOrdersSaved, orders.SaveCount);
    }

    // 失败类状态名：不建单、还原草稿，终态目标按失败归类写入 FinalizePending，并确认（ack）会话
    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    [InlineData("Failed", "Failed")]
    [InlineData("NotSubmitted", "Failed")]
    [InlineData("Cancelled", "Cancelled")]
    [InlineData("Canceled", "Cancelled")]
    public async Task RecoverAttemptAsync_failure_status_names_finalize_as_not_paid(string status, string expectedTarget)
    {
        var attempt = CreateAttempt("SESSION-MATRIX", "TXN-MATRIX-ATTEMPT");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus(status, "SESSION-MATRIX", "TXN-MATRIX-ATTEMPT", null, null)
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.Equal(CardRecoveryPhases.FinalizePending, attempts.RecoveryPhase);
        Assert.Equal(expectedTarget, attempts.RecoveryTargetStatus);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(1, backend.AcknowledgeCallCount);
    }

    // 批准金额核验：终端批准金额（卡交易明细按分）必须与 attempt 金额、订单草稿的卡金额都对得上
    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    // 相等 / 差一分 / 超出 / 缺失
    [InlineData(1000L, "10.00", "10.00", CardPaymentRecoveryOutcome.OrderCompleted)]
    [InlineData(1001L, "10.00", "10.00", CardPaymentRecoveryOutcome.Unknown)]
    [InlineData(999L, "10.00", "10.00", CardPaymentRecoveryOutcome.Unknown)]
    [InlineData(2000L, "10.00", "10.00", CardPaymentRecoveryOutcome.Unknown)]
    [InlineData(null, "10.00", "10.00", CardPaymentRecoveryOutcome.Unknown)]
    [InlineData(0L, "10.00", "10.00", CardPaymentRecoveryOutcome.Unknown)]
    // attempt 金额与草稿卡金额不一致：两头都要核验
    [InlineData(1000L, "9.99", "10.00", CardPaymentRecoveryOutcome.Unknown)]
    [InlineData(1000L, "10.00", "9.99", CardPaymentRecoveryOutcome.Unknown)]
    public async Task RecoverAttemptAsync_approved_amount_boundaries(
        long? cardAmountCents,
        string attemptAmountText,
        string draftCardAmountText,
        CardPaymentRecoveryOutcome expectedOutcome)
    {
        var draft = CreateDraft(
            actualAmount: 10m,
            cardAmount: decimal.Parse(draftCardAmountText, System.Globalization.CultureInfo.InvariantCulture));
        var attempt = CreateAttempt(
            "SESSION-MATRIX",
            "TXN-MATRIX-ATTEMPT",
            draft: draft,
            amount: decimal.Parse(attemptAmountText, System.Globalization.CultureInfo.InvariantCulture));
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var status = CreateStatus("Completed", "SESSION-MATRIX", "TXN-MATRIX-ATTEMPT", "00", "APPROVED", true) with
        {
            CardTransaction = CreateCloudCardTransaction(cardAmountCents, txnRef: null)
        };
        var backend = new FakeLinklyBackendTerminalClient { Status = status };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedOutcome == CardPaymentRecoveryOutcome.OrderCompleted ? 1 : 0, orders.SaveCount);
        Assert.Equal(expectedOutcome == CardPaymentRecoveryOutcome.OrderCompleted ? 1 : 0, backend.AcknowledgeCallCount);
    }

    [Fact]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    public async Task RecoverAttemptAsync_approved_without_card_transaction_or_notification_never_invents_the_amount()
    {
        var attempt = CreateAttempt("SESSION-MATRIX", "TXN-MATRIX-ATTEMPT");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus("Completed", "SESSION-MATRIX", "TXN-MATRIX-ATTEMPT", "00", "APPROVED", true)
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, backend.AcknowledgeCallCount);
    }

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    [InlineData(1000L, "", CardPaymentRecoveryOutcome.OrderCompleted)]
    [InlineData(1000L, "\"AmtTip\": 100, \"AmtCash\": 500, \"AmtSurcharge\": 20,", CardPaymentRecoveryOutcome.OrderCompleted)] // 只看 AmtPurchase
    [InlineData(1100L, "\"AmtTip\": 100,", CardPaymentRecoveryOutcome.Unknown)]
    [InlineData(1001L, "", CardPaymentRecoveryOutcome.Unknown)]
    public async Task RecoverAttemptAsync_legacy_notification_only_amount_boundaries_ignore_tip_cashout_and_surcharge(
        long amountMinor,
        string extraFields,
        CardPaymentRecoveryOutcome expectedOutcome)
    {
        var attempt = CreateAttempt("SESSION-MATRIX", "TXN-MATRIX-ATTEMPT");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var notification = new LinklyCloudBackendNotificationDto(
            "transaction",
            $$"""{ "Response": { "Success": true, "TxnRef": "TXN-MATRIX-ATTEMPT", "ResponseCode": "00", "ResponseText": "APPROVED", "AmtPurchase": {{amountMinor}}, {{extraFields}} "Dummy": 0 } }""",
            DateTimeOffset.Parse("2026-06-05T10:01:00+10:00"));
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus(
                "Completed",
                "SESSION-MATRIX",
                "TXN-MATRIX-ATTEMPT",
                "00",
                "APPROVED",
                true,
                notifications: [notification])
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(expectedOutcome, result.Outcome);
    }

    // ------------------------------------------------------------------
    // 主管结案不再做实时核验（用户 10-24 决定：人工确认为准）：
    // 无论后端会话处于哪种状态（已批准 / 已失败 / 仍在途 / 无结论），“确认未付款”与“确认已付款”都直接落库。
    // 原先按状态 → 结论（approved / failed / pending / inconclusive）拒绝其中一种或两种的矩阵不再成立。
    // ------------------------------------------------------------------

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    [InlineData("Completed", true, "00", "none", "approved")]
    [InlineData("completed", true, "00", "none", "approved")]
    [InlineData("Completed", false, "05", "none", "failed")]
    [InlineData("Completed", null, null, "none", "inconclusive")]    // Completed 缺成败位：无结论
    [InlineData("Completed", null, "00", "none", "inconclusive")]
    [InlineData("Completed", null, null, "approved", "inconclusive")]
    [InlineData("Failed", null, null, "none", "failed")]
    [InlineData("NotSubmitted", null, null, "none", "failed")]
    [InlineData("Cancelled", null, null, "none", "failed")]
    [InlineData("Canceled", null, null, "none", "failed")]
    [InlineData("SupervisorResolved", true, "00", "none", "inconclusive")] // 已被主管结案过：不会再有新结果
    [InlineData("Pending", null, null, "none", "pending")]
    [InlineData("Pending", null, null, "approved", "pending")]            // 这里 Pending + 批准通知仍按在途
    [InlineData("TokenRefreshRequired", null, null, "none", "pending")]
    [InlineData("Weird", true, "00", "none", "pending")]
    [InlineData(" Completed", true, "00", "none", "pending")]
    public async Task ResolvePaymentAsync_manual_decision_persists_for_every_backend_status(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string evidence,
        string expectedVerdict)
    {
        async Task<CardPaymentSupervisorResolutionResult> ResolveAsync(CardPaymentSupervisorDecision decision)
        {
            var attempt = CreateAttempt("S-LIVE-MATRIX", "TXN-LIVE-MATRIX", LocalCardPaymentAttemptStatus.Recovering);
            var attempts = new FakeCardPaymentAttemptRepository(attempt);
            var backend = new FakeLinklyBackendTerminalClient
            {
                Status = CreateStatus(
                    status,
                    "S-LIVE-MATRIX",
                    "TXN-LIVE-MATRIX",
                    responseCode,
                    responseCode is null ? null : "TEXT",
                    transactionSuccess,
                    MatrixEvidence(evidence))
            };
            var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);
            return await service.ResolvePaymentAsync(
                SupervisorResolution(attempt, decision, "BANK-REF-MATRIX"),
                new PosCartService(),
                Session);
        }

        var notPaid = await ResolveAsync(CardPaymentSupervisorDecision.ConfirmNotPaid);
        var paid = await ResolveAsync(CardPaymentSupervisorDecision.ConfirmPaid);

        // expectedVerdict 只描述这个状态在旧实时核验里的分类，保留数据行是为了证明每一种状态现在都不再拦截主管。
        Assert.True(notPaid.ResolutionPersisted, $"{expectedVerdict}: {notPaid.Message}");
        Assert.True(paid.ResolutionPersisted, $"{expectedVerdict}: {paid.Message}");
    }
}
