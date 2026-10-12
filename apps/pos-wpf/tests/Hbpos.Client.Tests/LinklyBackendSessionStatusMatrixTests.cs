using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

/// <summary>
/// Linkly 后端会话「已批准 / 已结束 / 批准金额核验」判定的状态矩阵（特征化测试）。
///
/// 背景：LinklyBackendTerminalClient（实时收款）、CardPaymentRecoveryService（异常中心恢复）、
/// CardTerminalService（ConfiguredCardTerminalClient.RecoverLinklyAsync）、CashPaymentWorkflowService（旧会话接管）
/// 各有一套判定。这里把每个判定出口在「全部已知状态值 × 结果字段 × 大小写/空白/未知值」上的当前行为逐格钉住，
/// 重构为共用分类器前后必须逐格一致；已知的不一致（例如恢复服务对 Pending+批准通知不认作终态）也如实记录，
/// 不在这里"修正"。
///
/// 本文件只覆盖 LinklyBackendTerminalClient 与 ConfiguredCardTerminalClient 两处出口；
/// 恢复服务与旧会话接管的矩阵分别在 CardPaymentRecoveryServiceTests.SessionStatusMatrix.cs 和
/// CashPaymentWorkflowServiceTask2BTests.SessionStatusMatrix.cs（复用各自私有夹具）。
/// </summary>
[Trait("Suite", "LinklySessionStatusMatrix")]
public sealed class LinklyBackendSessionStatusMatrixTests
{
    private const string SessionId = "matrix-session-1";
    private const string TxnRef = "P000000000000777";

    private static readonly PosSessionState PosSession = new("HB POS", "S001", "Main", "POS-01", "C001", "Alice", true, 0);

    private static readonly Guid AttemptGuid = Guid.Parse("f588a502-9129-e8ad-8627-3d9e1b2b892a");

    // ------------------------------------------------------------------
    // 夹具
    // ------------------------------------------------------------------

    private static LinklyCloudBackendSessionResponse Session(
        string status,
        bool? transactionSuccess = null,
        string? responseCode = null,
        string? responseText = null,
        string? txnRef = TxnRef,
        bool withCard = false,
        long? cardAmountCents = null,
        string? cardTxnRef = "inherit",
        string cardResponseCode = "00",
        string cardResponseText = "APPROVED",
        IReadOnlyList<LinklyCloudBackendNotificationDto>? notifications = null,
        string? receiptText = "RECEIPT")
    {
        var session = new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S001",
            "POS-01",
            SessionId,
            status,
            txnRef,
            responseCode,
            responseText,
            null,
            "DISPLAY",
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            null,
            receiptText,
            0,
            null,
            null,
            200,
            notifications ?? [],
            transactionSuccess);
        return withCard
            ? session with
            {
                CardTransaction = new LinklyCloudBackendCardTransactionDto(
                    cardTxnRef == "inherit" ? txnRef : cardTxnRef,
                    null,
                    "AUTH01",
                    "VISA",
                    "************1234",
                    "MID01",
                    cardResponseCode,
                    cardResponseText,
                    "000123",
                    DateTimeOffset.Parse("2026-06-05T10:01:00+10:00"),
                    cardAmountCents)
            }
            : session;
    }

    /// <summary>"transaction" 类型通知；金额与 Linkly 一致按分。extraFields 用来塞 AmtTip/AmtCash/AmtSurcharge 等无关字段。</summary>
    private static LinklyCloudBackendNotificationDto TransactionNotification(
        bool success = true,
        string txnRef = TxnRef,
        string responseCode = "00",
        string responseText = "APPROVED",
        long? amountMinor = 4000,
        string? txnType = "P",
        string extraFields = "")
    {
        var successText = success ? "true" : "false";
        var amount = amountMinor is long minor ? $"\"AmtPurchase\": {minor}," : string.Empty;
        var type = txnType is null ? string.Empty : $"\"TxnType\": \"{txnType}\",";
        var json =
            $$"""{ "Response": { "Success": {{successText}}, "TxnRef": "{{txnRef}}", "ResponseCode": "{{responseCode}}", "ResponseText": "{{responseText}}", {{amount}} {{type}} {{extraFields}} "Dummy": 0 } }""";
        return new LinklyCloudBackendNotificationDto("transaction", json, DateTimeOffset.Parse("2026-06-05T10:01:00+10:00"));
    }

    private static IReadOnlyList<LinklyCloudBackendNotificationDto> EvidenceNotifications(string evidence) => evidence switch
    {
        "none" => [],
        "approved" => [TransactionNotification()],
        "declined" => [TransactionNotification(success: false, responseCode: "05", responseText: "DECLINED")],
        _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null)
    };

    private static CardTerminalSettings CreateSettings() =>
        CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync,
            TerminalTimeout = TimeSpan.FromSeconds(5)
        };

    private static decimal Money(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    // 1. LinklyBackendTerminalClient.IsApprovedFinalTransaction（实时收款与旧会话接管共用的"批准"口径）
    // ------------------------------------------------------------------

    [Theory]
    // Completed + 成败位
    [InlineData("Completed", true, "00", "none", true)]
    [InlineData("Completed", true, null, "none", true)]
    [InlineData("Completed", true, "05", "none", true)]   // 成败位为准：TransactionSuccess=true 即使响应码不是批准码
    [InlineData("Completed", false, "00", "none", false)] // 明确失败压过批准码
    [InlineData("Completed", false, "05", "none", false)]
    [InlineData("Completed", false, null, "approved", false)]
    // Completed + 缺成败位：退回批准响应码 / 已核验批准通知兜底
    [InlineData("Completed", null, "00", "none", true)]
    [InlineData("Completed", null, "08", "none", true)]   // 签名核验批准
    [InlineData("Completed", null, "11", "none", true)]   // Approved VIP
    [InlineData("Completed", null, " 00 ", "none", true)] // 响应码会 Trim
    [InlineData("Completed", null, "0", "none", false)]
    [InlineData("Completed", null, "05", "none", false)]
    [InlineData("Completed", null, "TO", "none", false)]
    [InlineData("Completed", null, null, "none", false)]
    [InlineData("Completed", null, "   ", "none", false)]
    [InlineData("Completed", null, null, "approved", true)]
    [InlineData("Completed", null, null, "declined", false)]
    // 状态名大小写 / 空白
    [InlineData("completed", true, "00", "none", true)]
    [InlineData("COMPLETED", true, "00", "none", true)]
    [InlineData(" Completed", true, "00", "none", false)]
    [InlineData("Completed ", true, "00", "none", false)]
    // Pending：只有"已核验的批准通知"才算官方成功证据
    [InlineData("Pending", null, null, "approved", true)]
    [InlineData("Pending", true, null, "approved", true)]
    [InlineData("pending", null, null, "approved", true)]
    [InlineData("Pending", true, null, "none", false)]
    [InlineData("Pending", null, "00", "none", false)]
    [InlineData("Pending", false, null, "approved", false)]
    [InlineData("Pending", null, null, "declined", false)]
    [InlineData("Pending", null, null, "none", false)]
    // 其余状态名：不论字段多"成功"都不是批准
    [InlineData("Failed", true, "00", "approved", false)]
    [InlineData("NotSubmitted", true, "00", "approved", false)]
    [InlineData("Cancelled", true, "00", "approved", false)]
    [InlineData("Canceled", true, "00", "approved", false)]
    [InlineData("TokenRefreshRequired", true, "00", "approved", false)]
    [InlineData("SupervisorResolved", true, "00", "approved", false)]
    [InlineData("Weird", true, "00", "approved", false)]
    [InlineData("", true, "00", "approved", false)]
    public void IsApprovedFinalTransaction_matrix(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string evidence,
        bool expected)
    {
        var session = Session(
            status,
            transactionSuccess,
            responseCode,
            notifications: EvidenceNotifications(evidence));

        Assert.Equal(expected, LinklyBackendTerminalClient.IsApprovedFinalTransaction(session));
    }

    // ------------------------------------------------------------------
    // 2. LinklyBackendTerminalClient 实时轮询的"已结束"口径（IsFinal 是私有的，经 ResumeSessionUntilFinalAsync 观察：
    //    已结束 => 立即返回且不发任何 HTTP；未结束 => 会去 recover/轮询，探针处理器抛异常）
    // ------------------------------------------------------------------

    [Theory]
    // 失败类状态名：恒为终态，不看任何结果字段
    [InlineData("Failed", null, null, "none", true)]
    [InlineData("Failed", true, "00", "approved", true)]
    [InlineData("NotSubmitted", null, null, "none", true)]
    [InlineData("Cancelled", null, null, "none", true)]
    [InlineData("Canceled", null, null, "none", true)]
    [InlineData("cancelled", null, null, "none", true)]
    [InlineData("FAILED", null, null, "none", true)]
    // Completed：结果字段到位才算终态（OK/sendkey 后可能先返回 Completed、结果通知稍后才到）
    [InlineData("Completed", true, null, "none", true)]
    [InlineData("Completed", false, null, "none", true)]
    [InlineData("Completed", null, "00", "none", true)]
    [InlineData("Completed", null, "05", "none", true)]
    [InlineData("Completed", null, null, "approved", true)]
    [InlineData("Completed", null, null, "none", false)]
    [InlineData("Completed", null, "   ", "none", false)]
    [InlineData("Completed", null, null, "declined", false)]
    [InlineData("completed", true, null, "none", true)]
    [InlineData(" Completed", true, null, "none", false)]
    // Pending：已有官方批准证据才收尾，否则继续等
    [InlineData("Pending", null, null, "approved", true)]
    [InlineData("Pending", true, null, "approved", true)]
    [InlineData("Pending", false, null, "approved", false)]
    [InlineData("Pending", true, "00", "none", false)]
    [InlineData("Pending", null, null, "none", false)]
    // 非终态状态名与未知值：继续等待（最终以业务窗口超时转为结果未知）
    [InlineData("TokenRefreshRequired", true, "00", "approved", false)]
    [InlineData("SupervisorResolved", true, "00", "approved", false)]
    [InlineData("Weird", true, "00", "approved", false)]
    [InlineData("", true, "00", "approved", false)]
    public async Task Live_polling_IsFinal_matrix(
        string status,
        bool? transactionSuccess,
        string? responseCode,
        string evidence,
        bool expectedFinal)
    {
        var session = Session(
            status,
            transactionSuccess,
            responseCode,
            notifications: EvidenceNotifications(evidence));

        Assert.Equal(expectedFinal, await IsFinalAccordingToLiveClientAsync(session));
    }

    private static async Task<bool> IsFinalAccordingToLiveClientAsync(LinklyCloudBackendSessionResponse session)
    {
        var requests = 0;
        var handler = new ProbeHandler(() =>
        {
            requests++;
            throw new InvalidOperationException("probe: client tried to poll a non-final session");
        });
        var client = new LinklyBackendTerminalClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") },
            new NullDialogService(),
            TimeSpan.Zero,
            null,
            null,
            null,
            TimeSpan.FromSeconds(30),
            null,
            null);
        try
        {
            await client.ResumeSessionUntilFinalAsync(CreateSettings(), session);
            return requests == 0;
        }
        catch (InvalidOperationException) when (requests > 0)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 2b. 结算会话的"已结束"口径（私有的 IsSettlementFinal，经反射观察）：失败类状态名恒为终态；
    //     其余要看 OperationSuccess 与结算小票（失败时再给迟到的小票一个短宽限期）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Failed", null, false, 0, true)]
    [InlineData("failed", null, false, 0, true)]
    [InlineData("NotSubmitted", null, false, 0, true)]
    [InlineData("Cancelled", null, false, 0, true)]
    [InlineData("Canceled", null, false, 0, true)]
    [InlineData("cancelled", true, true, 0, true)]
    [InlineData("Completed", true, true, 0, true)]
    [InlineData("Completed", true, false, 0, false)]    // 成功的完成通知先到、小票未到：继续等
    [InlineData("Completed", false, false, 0, false)]   // 失败且没有小票：给迟到小票宽限期
    [InlineData("Completed", false, false, 4, false)]
    [InlineData("Completed", false, false, 5, true)]    // 宽限期（5 次）用完：收尾
    [InlineData("Completed", false, true, 0, true)]     // 失败但有小票：立即收尾
    [InlineData("Completed", null, true, 0, false)]     // 没有 OperationSuccess：未决
    [InlineData("Pending", null, false, 0, false)]
    [InlineData("Pending", true, true, 0, true)]        // 状态名不是终态，但结果字段与小票已齐：按字段收尾
    [InlineData("TokenRefreshRequired", null, false, 9, false)]
    [InlineData("SupervisorResolved", null, false, 9, false)]
    [InlineData(" Failed", null, false, 0, false)]      // 状态名不 Trim
    [InlineData("Weird", null, false, 0, false)]
    public void Settlement_IsSettlementFinal_matrix(
        string status,
        bool? operationSuccess,
        bool hasReceipt,
        int failureWaitPolls,
        bool expected)
    {
        var session = Session(status) with
        {
            OperationType = "Settlement",
            OperationSuccess = operationSuccess,
            SettlementReceiptTexts = hasReceipt ? ["SETTLEMENT RECEIPT"] : null
        };
        var method = typeof(LinklyBackendTerminalClient).GetMethod(
            "IsSettlementFinal",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Equal(expected, (bool)method!.Invoke(null, [session, failureWaitPolls])!);
    }

    // ------------------------------------------------------------------
    // 3. 已到终态的批准金额核验：HasFinalApprovalEvidenceMatchingAttempt
    //    （同一套 ReadTransactionResult + IsTransactionResultVerified；恢复服务 / ConfiguredCardTerminalClient 手写调用同一对函数）
    // ------------------------------------------------------------------

    [Theory]
    // 金额（卡交易明细按分）：相等 / 差一分 / 超出 / 缺失
    [InlineData(4000L, "40.00", true)]
    [InlineData(4001L, "40.00", false)]
    [InlineData(3999L, "40.00", false)]
    [InlineData(5000L, "40.00", false)]
    [InlineData(null, "40.00", false)]
    [InlineData(0L, "0.00", true)]       // 零金额批准：相等即通过（边界，如实记录）
    [InlineData(-4000L, "40.00", false)] // 终端金额为负：直接不通过
    // 请求金额取绝对值（退款草稿金额为负）
    [InlineData(4000L, "-40.00", true)]
    [InlineData(4001L, "-40.00", false)]
    // 比较前两边都按"分"四舍五入（AwayFromZero）：请求金额的亚分噪声只要舍入后同分就通过
    [InlineData(4000L, "40.004", true)]
    [InlineData(4000L, "40.005", false)]
    [InlineData(4000L, "39.996", true)]
    [InlineData(4000L, "39.994", false)]
    public void HasFinalApproval_card_transaction_amount_boundaries(
        long? cardAmountCents,
        string expectedAmountText,
        bool expected)
    {
        var session = Session(
            "Completed",
            true,
            "00",
            "APPROVED",
            withCard: true,
            cardAmountCents: cardAmountCents);

        Assert.Equal(
            expected,
            LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, TxnRef, Money(expectedAmountText)));
    }

    [Theory]
    // 老服务端不返回卡交易明细、只带原始 transaction 通知：金额读 AmtPurchase（按分）
    [InlineData(4000L, "", "40.00", true)]
    [InlineData(4001L, "", "40.00", false)]
    [InlineData(3999L, "", "40.00", false)]
    [InlineData(null, "", "40.00", false)]
    // 终端可能同时回 AmtTip / AmtCash / AmtSurcharge：核验只看 AmtPurchase，其余字段既不加也不减
    [InlineData(4000L, "\"AmtTip\": 400, \"AmtCash\": 1000, \"AmtSurcharge\": 50,", "40.00", true)]
    [InlineData(4400L, "\"AmtTip\": 400,", "40.00", false)]           // 终端把小费并进 AmtPurchase：对不上即不通过
    [InlineData(4000L, "\"AmtTip\": 400,", "44.00", false)]           // 请求金额含小费而 AmtPurchase 不含：同样不通过
    [InlineData(4000L, "\"AmtCash\": 1000,", "50.00", false)]         // 含 cash-out 的请求总额不会被 AmtCash 补足
    [InlineData(4000L, "\"AmtSurcharge\": 80,", "40.80", false)]
    public void HasFinalApproval_notification_amount_boundaries_ignore_tip_cashout_and_surcharge(
        long? amountMinor,
        string extraFields,
        string expectedAmountText,
        bool expected)
    {
        var session = Session(
            "Completed",
            true,
            "00",
            "APPROVED",
            notifications: [TransactionNotification(amountMinor: amountMinor, extraFields: extraFields)]);

        Assert.Equal(
            expected,
            LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, TxnRef, Money(expectedAmountText)));
    }

    [Fact]
    public void HasFinalApproval_without_card_transaction_and_without_notification_is_not_verified()
    {
        // 绝不能用请求金额补造终端批准金额。
        var session = Session("Completed", true, "00", "APPROVED");

        Assert.False(LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, TxnRef, 40m));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("pending")]
    public void HasFinalApproval_leaves_pending_sessions_to_the_pending_evidence_check(string status)
    {
        // Pending 的批准证据由 HasPendingApprovalEvidenceMatchingAttempt 负责；终态核验对 Pending 不设防。
        var session = Session(status);

        Assert.True(LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, TxnRef, 40m));
    }

    [Fact]
    public void HasFinalApproval_does_not_compare_the_expected_txn_ref_argument()
    {
        // 特征化：expectedTxnRef 参数当前没有参与比较（ReadTransactionResult 不使用它），
        // 终端引用只与会话自带的 TxnRef 互相印证。重构不得悄悄改变这一点。
        var session = Session("Completed", true, "00", "APPROVED", withCard: true, cardAmountCents: 4000);

        Assert.True(LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, "SOMETHING-ELSE", 40m));
        Assert.True(LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, null, 40m));
    }

    [Theory]
    // 终端引用与会话引用互相印证
    [InlineData("P000000000000777", "inherit", true)]
    [InlineData("P000000000000777", null, true)]               // 卡明细缺引用：沿用会话引用
    [InlineData("P000000000000777", "P000000000000999", false)] // 两边不一致
    [InlineData(null, "inherit", true)]                        // 会话缺引用而卡明细有：以卡明细为准
    [InlineData(null, null, false)]                            // 两边都没有引用：无法核验
    [InlineData("P000000000000777", "   ", true)]              // 空白引用等同缺失，沿用会话引用
    public void HasFinalApproval_requires_a_consistent_terminal_txn_ref(
        string? sessionTxnRef,
        string? cardTxnRef,
        bool expected)
    {
        var resolvedCardTxnRef = cardTxnRef == "inherit" ? TxnRef : cardTxnRef;
        var session = Session(
            "Completed",
            true,
            "00",
            "APPROVED",
            txnRef: sessionTxnRef,
            withCard: true,
            cardAmountCents: 4000,
            cardTxnRef: resolvedCardTxnRef);

        Assert.Equal(expected, LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, TxnRef, 40m));
    }

    [Theory]
    // 会话受保护的响应码 / 文案不得被卡明细覆盖成另一个终态
    [InlineData("00", "APPROVED", "00", "APPROVED", true)]
    [InlineData("00", "APPROVED", "05", "APPROVED", false)]
    [InlineData("00", "APPROVED", "00", "DECLINED", false)]
    [InlineData("00", "approved", "00", "APPROVED", true)]  // 文案比较忽略大小写
    [InlineData(null, null, "00", "APPROVED", true)]        // 会话没有响应码/文案：以卡明细为准
    [InlineData(null, null, "05", "DECLINED", false)]       // 卡明细的响应码不是批准码
    public void HasFinalApproval_requires_card_details_to_agree_with_the_protected_result(
        string? sessionCode,
        string? sessionText,
        string cardCode,
        string cardText,
        bool expected)
    {
        var session = Session(
            "Completed",
            true,
            sessionCode,
            sessionText,
            withCard: true,
            cardAmountCents: 4000,
            cardResponseCode: cardCode,
            cardResponseText: cardText);

        Assert.Equal(expected, LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, TxnRef, 40m));
    }

    // ------------------------------------------------------------------
    // 4. Pending 会话的批准证据核验：HasPendingApprovalEvidenceMatchingAttempt
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Weird")]
    public void HasPendingApproval_is_vacuously_true_for_non_pending_sessions(string status)
    {
        var session = Session(status);

        Assert.True(LinklyBackendTerminalClient.HasPendingApprovalEvidenceMatchingAttempt(session, TxnRef, 40m, "P"));
    }

    [Theory]
    // 金额
    [InlineData(4000L, "P", "40.00", "P", true)]
    [InlineData(4001L, "P", "40.00", "P", false)]
    [InlineData(3999L, "P", "40.00", "P", false)]
    [InlineData(4000L, "P", "-40.00", "P", true)]
    // 交易类型：双方都有才比较，忽略大小写；任一方缺失放行
    [InlineData(4000L, "R", "40.00", "P", false)]
    [InlineData(4000L, "p", "40.00", "P", true)]
    [InlineData(4000L, null, "40.00", "P", true)]
    [InlineData(4000L, "P", "40.00", null, true)]
    public void HasPendingApproval_checks_amount_and_txn_type(
        long amountMinor,
        string? notificationTxnType,
        string expectedAmountText,
        string? expectedTxnType,
        bool expected)
    {
        var session = Session(
            "Pending",
            notifications: [TransactionNotification(amountMinor: amountMinor, txnType: notificationTxnType)]);

        Assert.Equal(
            expected,
            LinklyBackendTerminalClient.HasPendingApprovalEvidenceMatchingAttempt(
                session,
                TxnRef,
                Money(expectedAmountText),
                expectedTxnType));
    }

    [Theory]
    [InlineData("P000000000000777", true)]
    [InlineData("p000000000000777", false)] // 引用比较区分大小写（Ordinal）
    [InlineData("P000000000000999", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void HasPendingApproval_requires_the_expected_txn_ref_to_match_the_notification(
        string? expectedTxnRef,
        bool expected)
    {
        var session = Session("Pending", notifications: [TransactionNotification()]);

        Assert.Equal(
            expected,
            LinklyBackendTerminalClient.HasPendingApprovalEvidenceMatchingAttempt(session, expectedTxnRef, 40m, "P"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("declined")]
    public void HasPendingApproval_without_a_verified_approved_notification_is_false(string evidence)
    {
        var session = Session("Pending", notifications: EvidenceNotifications(evidence));

        Assert.False(LinklyBackendTerminalClient.HasPendingApprovalEvidenceMatchingAttempt(session, TxnRef, 40m, "P"));
    }

    // ------------------------------------------------------------------
    // 5. ConfiguredCardTerminalClient.RecoverLinklyAsync（后端模式）：状态 × 结果 的出口
    //    approved = 批准且核验通过；rejected = 明确未批准（非 ResultUnknown）；unknown = 结果未知
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Completed", true, "approved")]
    [InlineData("completed", true, "approved")]
    [InlineData("COMPLETED", true, "approved")]
    [InlineData("Completed", false, "rejected")]
    [InlineData("Completed", null, "unknown")]             // Completed 缺成败位：不认作终态，保持未知
    [InlineData(" Completed", true, "unknown")]            // 状态名不 Trim
    [InlineData("Completed ", true, "unknown")]
    [InlineData("Failed", null, "rejected")]
    [InlineData("Failed", true, "rejected")]
    [InlineData("failed", false, "rejected")]
    [InlineData("NotSubmitted", null, "rejected")]
    [InlineData("Cancelled", null, "rejected")]
    [InlineData("Canceled", null, "rejected")]
    [InlineData("cancelled", null, "rejected")]
    [InlineData("Pending", null, "unknown")]
    [InlineData("Pending", true, "unknown")]               // 这里不接受 Pending + 成功位
    [InlineData("TokenRefreshRequired", null, "unknown")]
    [InlineData("SupervisorResolved", true, "unknown")]
    [InlineData("Weird", true, "unknown")]
    [InlineData("", true, "unknown")]
    public async Task Configured_client_backend_recovery_status_matrix(
        string status,
        bool? transactionSuccess,
        string expected)
    {
        var session = Session(
            status,
            transactionSuccess,
            transactionSuccess == false ? "05" : "00",
            transactionSuccess == false ? "DECLINED" : "APPROVED",
            withCard: true,
            cardAmountCents: 4000);

        var result = await RecoverThroughConfiguredClientAsync(session);

        AssertRecoveryKind(expected, result);
    }

    [Fact]
    public async Task Configured_client_backend_recovery_completed_without_success_flag_stays_unknown_even_with_an_approval_code()
    {
        // Completed + 缺成败位 + 批准响应码：实时收款会认作批准，这里（以及恢复服务）仍是未知。
        var session = Session("Completed", null, "00", "APPROVED", withCard: true, cardAmountCents: 4000);

        AssertRecoveryKind("unknown", await RecoverThroughConfiguredClientAsync(session));
    }

    [Theory]
    [InlineData(4000L, "approved")]
    [InlineData(4001L, "unknown")]
    [InlineData(3999L, "unknown")]
    [InlineData(5000L, "unknown")]
    [InlineData(null, "unknown")]
    [InlineData(0L, "unknown")]
    public async Task Configured_client_backend_recovery_amount_boundaries_use_the_card_transaction(
        long? cardAmountCents,
        string expected)
    {
        var session = Session("Completed", true, "00", "APPROVED", withCard: true, cardAmountCents: cardAmountCents);

        AssertRecoveryKind(expected, await RecoverThroughConfiguredClientAsync(session));
    }

    [Theory]
    [InlineData(4000L, "", "approved")]
    [InlineData(4000L, "\"AmtTip\": 400, \"AmtCash\": 1000, \"AmtSurcharge\": 50,", "approved")]
    [InlineData(4400L, "\"AmtTip\": 400,", "unknown")]
    [InlineData(4001L, "", "unknown")]
    [InlineData(null, "", "unknown")]
    public async Task Configured_client_backend_recovery_amount_boundaries_use_the_transaction_notification(
        long? amountMinor,
        string extraFields,
        string expected)
    {
        var session = Session(
            "Completed",
            true,
            "00",
            "APPROVED",
            notifications: [TransactionNotification(amountMinor: amountMinor, extraFields: extraFields)]);

        AssertRecoveryKind(expected, await RecoverThroughConfiguredClientAsync(session));
    }

    [Fact]
    public async Task Configured_client_backend_recovery_without_any_amount_evidence_stays_unknown()
    {
        var session = Session("Completed", true, "00", "APPROVED");

        AssertRecoveryKind("unknown", await RecoverThroughConfiguredClientAsync(session));
    }

    [Fact]
    public async Task Configured_client_backend_recovery_rejection_reports_the_session_response()
    {
        var session = Session("Completed", false, "05", "DECLINED", withCard: true, cardAmountCents: 4000);

        var result = await RecoverThroughConfiguredClientAsync(session);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("DECLINED", result.Message);
        Assert.Equal("05", result.ResponseCode);
        Assert.Equal("DECLINED", result.ResponseText);
    }

    [Fact]
    public async Task Configured_client_backend_recovery_rejection_without_response_text_uses_the_default_message()
    {
        var session = Session("Cancelled", null, null, null);

        var result = await RecoverThroughConfiguredClientAsync(session);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("Linkly transaction was not approved.", result.Message);
    }

    private static void AssertRecoveryKind(string expected, PaymentAuthorizationResult result)
    {
        switch (expected)
        {
            case "approved":
                Assert.True(result.Approved);
                Assert.False(result.ResultUnknown);
                break;
            case "rejected":
                Assert.False(result.Approved);
                Assert.False(result.ResultUnknown);
                break;
            case "unknown":
                Assert.False(result.Approved);
                Assert.True(result.ResultUnknown);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected), expected, null);
        }
    }

    private static Task<PaymentAuthorizationResult> RecoverThroughConfiguredClientAsync(
        LinklyCloudBackendSessionResponse status)
    {
        var settings = CreateSettings();
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new ProbeHandler(() => throw new InvalidOperationException("HTTP must not be called."))),
            linklyBackendTerminalClient: new FakeBackend(status));
        return client.RecoverLinklyAsync(CreateAttempt(status.SessionId), PosSession);
    }

    private static LocalCardPaymentAttempt CreateAttempt(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalCardPaymentAttempt(
            AttemptGuid,
            sessionId,
            TxnRef,
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            CardTerminalSettings.FormatLinklyConnectionMode(LinklyConnectionMode.CloudBackendAsync),
            "P",
            40m,
            LocalCardPaymentAttemptStatus.Recovering,
            "{}",
            "S001",
            "POS-01",
            "C001",
            null,
            null,
            null,
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            null,
            null,
            "Repayment",
            Guid.NewGuid());
    }

    // ------------------------------------------------------------------
    // 测试替身
    // ------------------------------------------------------------------

    private sealed class ProbeHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            throw new InvalidOperationException("unreachable: probe handler always throws");
        }
    }

    private sealed class NullDialogService : ILinklyTerminalDialogService
    {
        public CancellationToken LocalCancelToken => CancellationToken.None;

        public Task<LinklyTerminalDialogAction?> UpdateAsync(
            LinklyTerminalDialogState state,
            CancellationToken cancellationToken) =>
            Task.FromResult<LinklyTerminalDialogAction?>(null);

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeBackend(LinklyCloudBackendSessionResponse status) : ILinklyBackendTerminalClient
    {
        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
            CardTerminalSettings settings,
            LinklyCloudBackendSessionResponse activeStatus,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task AcknowledgeSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AcknowledgeSupervisorResolvedSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
