using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

// Linkly 后端会话「已批准 / 已结束 / 批准金额核验」状态矩阵（特征化测试）之一：新刷卡前接管旧活动会话这一处。
// 另外三处的矩阵见 LinklyBackendSessionStatusMatrixTests.cs 与 CardPaymentRecoveryServiceTests.SessionStatusMatrix.cs。
// 接管的"终态"口径是：IsApprovedFinalTransaction（含 Pending + 已核验批准通知）或状态名 Completed/Failed/NotSubmitted/Cancelled/Canceled，
// 批准之外的失败归类按 "Status ResponseText" 文本；重构为共用分类器前后必须逐格一致。
public sealed partial class CashPaymentWorkflowServiceTask2BTests
{
    private sealed record TakeoverObservation(
        bool Succeeded,
        LocalCardPaymentAttemptStatus? OldAttemptStatus,
        bool Acknowledged);

    private static LinklyCloudBackendSessionResponse MatrixFinalStatus(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string? responseText,
        bool approvedNotification = false,
        long? cardAmountCents = null)
    {
        var session = FinalApprovedSession("active-session-1", "TXN-OLD", transactionSuccess) with
        {
            Status = status,
            ResponseCode = responseCode,
            ResponseText = responseText,
            DisplayText = responseText
        };
        if (approvedNotification)
        {
            session = session with { Notifications = PendingApprovedSession("active-session-1", "TXN-OLD").Notifications };
        }

        return cardAmountCents is long cents
            ? session with { CardTransaction = VerifiedCardTransaction("TXN-OLD", cents) }
            : session;
    }

    private static async Task<TakeoverObservation> ObserveTakeoverAsync(
        LinklyCloudBackendSessionResponse finalStatus,
        LocalCardPaymentAttempt? existing = null,
        LinklyCloudBackendSessionResponse? activeStatus = null)
    {
        var events = new List<string>();
        var attempts = new RecordingAttemptRepository(events, initialAttempts: existing is null ? null : [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, finalStatus);
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            activeStatus ?? ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);

        await RunNewCardPaymentAsync(workflow, AckSession);

        var old = existing is null
            ? attempts.Attempts.SingleOrDefault(attempt => attempt.OperationKind == "ActiveSession")
            : attempts.Attempts.Single(attempt => attempt.AttemptGuid == existing.AttemptGuid);
        return new TakeoverObservation(
            terminal.TakeoverResult?.Succeeded == true,
            old?.Status,
            events.Contains("acknowledge"));
    }

    // ------------------------------------------------------------------
    // 无权威 attempt（generic ActiveSession）：状态 × 结果字段 → 接管是否成功 / 落库的终态
    // 已批准的 generic 记录一律降级为 RequiresReview（不自动落单），但仍确认会话以释放终端。
    // ------------------------------------------------------------------

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    // 批准：Completed + 成败位 / 批准码 / 已核验批准通知（任一）
    [InlineData("Completed", true, "00", "APPROVED", false, true, LocalCardPaymentAttemptStatus.RequiresReview)]
    [InlineData("completed", true, "00", "APPROVED", false, true, LocalCardPaymentAttemptStatus.RequiresReview)]
    [InlineData("Completed", true, "05", "DECLINED", false, true, LocalCardPaymentAttemptStatus.RequiresReview)]
    [InlineData("Completed", null, "00", "APPROVED", false, true, LocalCardPaymentAttemptStatus.RequiresReview)]
    [InlineData("Completed", null, "08", "APPROVED", false, true, LocalCardPaymentAttemptStatus.RequiresReview)]
    [InlineData("Completed", null, "11", "APPROVED", false, true, LocalCardPaymentAttemptStatus.RequiresReview)]
    [InlineData("Completed", null, null, null, true, true, LocalCardPaymentAttemptStatus.RequiresReview)]
    // Completed 但不是批准：按文本归类（成败位=false 压过批准码）
    [InlineData("Completed", false, "05", "DECLINED", false, true, LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("Completed", false, "00", "APPROVED", false, true, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Completed", false, "TO", "TIMEOUT", false, true, LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("Completed", false, "17", "CANCELLED BY USER", false, true, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Completed", false, "50", "SYSTEM ERROR", false, true, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Completed", null, "05", "DECLINED", false, true, LocalCardPaymentAttemptStatus.Declined)]
    // 接管侧把"Completed 且完全没有结果字段"也当终态并按失败落库（实时轮询不会把它当终态，所以生产中走不到这格）
    [InlineData("Completed", null, null, null, false, true, LocalCardPaymentAttemptStatus.Failed)]
    // 失败类状态名
    [InlineData("Failed", null, null, null, false, true, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Failed", true, "00", "APPROVED", false, true, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("NotSubmitted", null, null, null, false, true, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Cancelled", null, null, null, false, true, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Canceled", null, null, null, false, true, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("cancelled", null, null, null, false, true, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Failed", null, null, "Transaction timeout", false, true, LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("Failed", null, null, "CARD DECLINED", false, true, LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("Failed", null, null, "timeout then cancelled", false, true, LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("Failed", null, null, "cancel or declined", false, true, LocalCardPaymentAttemptStatus.Cancelled)]
    // 非终态与未知值：接管失败（不确认、不发起新扣款）；generic 记录仍已创建并保持 Recovering
    [InlineData("Pending", null, null, null, false, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData("Pending", true, "00", "APPROVED", false, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData("TokenRefreshRequired", null, null, null, false, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData("SupervisorResolved", true, "00", "APPROVED", false, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData("Weird", true, "00", "APPROVED", false, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData("", true, "00", "APPROVED", false, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(" Completed", true, "00", "APPROVED", false, false, LocalCardPaymentAttemptStatus.Recovering)]
    public async Task Takeover_of_generic_active_session_status_matrix(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string? responseText,
        bool approvedNotification,
        bool expectedSucceeded,
        LocalCardPaymentAttemptStatus expectedAttemptStatus)
    {
        var finalStatus = MatrixFinalStatus(status, transactionSuccess, responseCode, responseText, approvedNotification);

        var observed = await ObserveTakeoverAsync(finalStatus);

        Assert.Equal(expectedSucceeded, observed.Succeeded);
        Assert.Equal(expectedSucceeded, observed.Acknowledged);
        Assert.Equal(expectedAttemptStatus, observed.OldAttemptStatus);
    }

    // Pending + 已核验的批准通知：接管侧认作终态批准；generic 记录的核验金额取自"旧会话摘要里的卡交易明细"（没有则为 0，对不上即失败）
    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    [InlineData(1000L, 1000L, true, LocalCardPaymentAttemptStatus.RequiresReview)]
    [InlineData(1000L, 1001L, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(1000L, 999L, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(1000L, null, false, LocalCardPaymentAttemptStatus.Recovering)]
    public async Task Takeover_of_generic_pending_approval_verifies_the_notification_amount_against_the_active_summary(
        long notificationCents,
        long? activeSummaryCents,
        bool expectedSucceeded,
        LocalCardPaymentAttemptStatus expectedAttemptStatus)
    {
        var activeStatus = ActivePendingSession("active-session-1", "TXN-OLD");
        if (activeSummaryCents is long cents)
        {
            activeStatus = activeStatus with { CardTransaction = VerifiedCardTransaction("TXN-OLD", cents) };
        }

        var finalStatus = PendingApprovedSession("active-session-1", "TXN-OLD", amountMinor: notificationCents);

        var observed = await ObserveTakeoverAsync(finalStatus, activeStatus: activeStatus);

        Assert.Equal(expectedSucceeded, observed.Succeeded);
        Assert.Equal(expectedSucceeded, observed.Acknowledged);
        Assert.Equal(expectedAttemptStatus, observed.OldAttemptStatus);
    }

    // ------------------------------------------------------------------
    // 有权威 Sale attempt（金额 10.00）：已批准必须通过终态金额核验才落 Approved 并确认会话
    // ------------------------------------------------------------------

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    // 卡交易明细（按分）：相等 / 差一分 / 超出 / 缺失
    [InlineData(1000L, true, LocalCardPaymentAttemptStatus.Approved)]
    [InlineData(1001L, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(999L, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(2000L, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(null, false, LocalCardPaymentAttemptStatus.Recovering)]
    public async Task Takeover_of_sale_attempt_verifies_the_final_approved_amount(
        long? cardAmountCents,
        bool expectedSucceeded,
        LocalCardPaymentAttemptStatus expectedAttemptStatus)
    {
        var session = AckSession;
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", session, Guid.NewGuid());
        var finalStatus = MatrixFinalStatus("Completed", true, "00", "APPROVED", cardAmountCents: cardAmountCents);

        var observed = await ObserveTakeoverAsync(finalStatus, existing);

        Assert.Equal(expectedSucceeded, observed.Succeeded);
        Assert.Equal(expectedSucceeded, observed.Acknowledged);
        Assert.Equal(expectedAttemptStatus, observed.OldAttemptStatus);
    }

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    // 老服务端：没有卡交易明细，金额取自原始 transaction 通知的 AmtPurchase
    [InlineData(1000L, true, LocalCardPaymentAttemptStatus.Approved)]
    [InlineData(1001L, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(999L, false, LocalCardPaymentAttemptStatus.Recovering)]
    public async Task Takeover_of_sale_attempt_verifies_the_notification_amount_when_no_card_transaction(
        long amountMinor,
        bool expectedSucceeded,
        LocalCardPaymentAttemptStatus expectedAttemptStatus)
    {
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", AckSession, Guid.NewGuid());
        var finalStatus = MatrixFinalStatus("Completed", true, "00", "APPROVED") with
        {
            Notifications = PendingApprovedSession("active-session-1", "TXN-OLD", amountMinor: amountMinor).Notifications
        };

        var observed = await ObserveTakeoverAsync(finalStatus, existing);

        Assert.Equal(expectedSucceeded, observed.Succeeded);
        Assert.Equal(expectedSucceeded, observed.Acknowledged);
        Assert.Equal(expectedAttemptStatus, observed.OldAttemptStatus);
    }

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    // Pending + 通知：金额与交易类型都要对得上
    [InlineData(1000L, true, LocalCardPaymentAttemptStatus.Approved)]
    [InlineData(1001L, false, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(999L, false, LocalCardPaymentAttemptStatus.Recovering)]
    public async Task Takeover_of_sale_attempt_verifies_the_pending_approval_notification_amount(
        long amountMinor,
        bool expectedSucceeded,
        LocalCardPaymentAttemptStatus expectedAttemptStatus)
    {
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", AckSession, Guid.NewGuid());
        var finalStatus = PendingApprovedSession("active-session-1", "TXN-OLD", amountMinor: amountMinor);

        var observed = await ObserveTakeoverAsync(finalStatus, existing);

        Assert.Equal(expectedSucceeded, observed.Succeeded);
        Assert.Equal(expectedSucceeded, observed.Acknowledged);
        Assert.Equal(expectedAttemptStatus, observed.OldAttemptStatus);
    }

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    // 没有金额证据的 Completed + 成败位：不能用 attempt 金额补造终端批准金额
    [InlineData("Completed", true, "00", "APPROVED")]
    [InlineData("Completed", null, "00", "APPROVED")]
    public async Task Takeover_of_sale_attempt_without_any_amount_evidence_is_not_acknowledged(
        string status,
        bool? transactionSuccess,
        string responseCode,
        string responseText)
    {
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", AckSession, Guid.NewGuid());
        var finalStatus = MatrixFinalStatus(status, transactionSuccess, responseCode, responseText);

        var observed = await ObserveTakeoverAsync(finalStatus, existing);

        Assert.False(observed.Succeeded);
        Assert.False(observed.Acknowledged);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, observed.OldAttemptStatus);
    }

    [Theory]
    [Trait("Suite", "LinklySessionStatusMatrix")]
    // 未批准的终态不需要金额证据，权威 attempt 照常落失败类终态并确认会话
    [InlineData("Completed", false, "05", "DECLINED", LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("Failed", null, null, null, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("NotSubmitted", null, null, null, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Cancelled", null, null, null, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Canceled", null, null, null, LocalCardPaymentAttemptStatus.Cancelled)]
    public async Task Takeover_of_sale_attempt_persists_unpaid_terminal_outcomes(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string? responseText,
        LocalCardPaymentAttemptStatus expectedAttemptStatus)
    {
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", AckSession, Guid.NewGuid());
        var finalStatus = MatrixFinalStatus(status, transactionSuccess, responseCode, responseText);

        var observed = await ObserveTakeoverAsync(finalStatus, existing);

        Assert.True(observed.Succeeded);
        Assert.True(observed.Acknowledged);
        Assert.Equal(expectedAttemptStatus, observed.OldAttemptStatus);
    }
}
