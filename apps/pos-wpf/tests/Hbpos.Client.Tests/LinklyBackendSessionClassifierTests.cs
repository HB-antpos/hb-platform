using System.Globalization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

/// <summary>
/// <see cref="LinklyBackendSessionClassifier"/> 本身的矩阵：所有已知状态值（含大小写、空白、未知值、空）× 各判定出口。
/// 四处调用方（实时收款 / 恢复服务 / ConfiguredCardTerminalClient / 旧会话接管）的逐格行为见
/// LinklyBackendSessionStatusMatrixTests 及其两个 partial 兄弟文件。
/// </summary>
[Trait("Suite", "LinklySessionStatusMatrix")]
public sealed class LinklyBackendSessionClassifierTests
{
    private const string TxnRef = "P000000000000777";

    private static readonly string?[] AllStatuses =
    [
        "Pending", "Completed", "Failed", "NotSubmitted", "Cancelled", "Canceled",
        "TokenRefreshRequired", "SupervisorResolved",
        "completed", "COMPLETED", "cancelled", "CANCELED", "failed", "notsubmitted", "pending",
        " Completed", "Completed ", " Failed", "Weird", "", " ", null
    ];

    private static readonly bool?[] AllSuccessFlags = [true, false, null];

    private static LinklyCloudBackendSessionResponse Session(
        string? status,
        bool? transactionSuccess = null,
        string? responseCode = null,
        string? responseText = null,
        bool withCard = false,
        long? cardAmountCents = null,
        string? notificationAmountMinor = null) =>
        new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S001",
            "POS-01",
            "classifier-session",
            status!,
            TxnRef,
            responseCode,
            responseText,
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
            "RECEIPT",
            0,
            null,
            null,
            200,
            notificationAmountMinor is null
                ? []
                :
                [
                    new LinklyCloudBackendNotificationDto(
                        "transaction",
                        $$"""{ "Response": { "Success": true, "TxnRef": "{{TxnRef}}", "ResponseCode": "00", "ResponseText": "APPROVED", "AmtPurchase": {{notificationAmountMinor}}, "AmtTip": 300, "AmtCash": 2000, "AmtSurcharge": 40 } }""",
                        DateTimeOffset.Parse("2026-06-05T10:01:00+10:00"))
                ],
            transactionSuccess)
        {
            CardTransaction = withCard
                ? new LinklyCloudBackendCardTransactionDto(
                    TxnRef,
                    null,
                    "AUTH01",
                    "VISA",
                    "************1234",
                    "MID01",
                    "00",
                    "APPROVED",
                    "000123",
                    DateTimeOffset.Parse("2026-06-05T10:01:00+10:00"),
                    cardAmountCents)
                : null
        };

    // ------------------------------------------------------------------
    // 状态名
    // ------------------------------------------------------------------

    [Theory]
    //           status                 Completed Pending NotSubmitted Cancelled Failure  Terminal
    [InlineData("Completed",            true,     false,  false,       false,    false,   true)]
    [InlineData("completed",            true,     false,  false,       false,    false,   true)]
    [InlineData("COMPLETED",            true,     false,  false,       false,    false,   true)]
    [InlineData("Pending",              false,    true,   false,       false,    false,   false)]
    [InlineData("pending",              false,    true,   false,       false,    false,   false)]
    [InlineData("Failed",               false,    false,  false,       false,    true,    true)]
    [InlineData("failed",               false,    false,  false,       false,    true,    true)]
    [InlineData("NotSubmitted",         false,    false,  true,        false,    true,    true)]
    [InlineData("notsubmitted",         false,    false,  true,        false,    true,    true)]
    [InlineData("Cancelled",            false,    false,  false,       true,     true,    true)]
    [InlineData("Canceled",             false,    false,  false,       true,     true,    true)]
    [InlineData("cancelled",            false,    false,  false,       true,     true,    true)]
    [InlineData("CANCELED",             false,    false,  false,       true,     true,    true)]
    [InlineData("TokenRefreshRequired", false,    false,  false,       false,    false,   false)]
    [InlineData("SupervisorResolved",   false,    false,  false,       false,    false,   false)]
    [InlineData("Weird",                false,    false,  false,       false,    false,   false)]
    [InlineData(" Completed",           false,    false,  false,       false,    false,   false)] // 状态名不 Trim
    [InlineData("Completed ",           false,    false,  false,       false,    false,   false)]
    [InlineData(" Failed",              false,    false,  false,       false,    false,   false)]
    [InlineData("",                     false,    false,  false,       false,    false,   false)]
    [InlineData(" ",                    false,    false,  false,       false,    false,   false)]
    [InlineData(null,                   false,    false,  false,       false,    false,   false)]
    public void Status_name_predicates(
        string? status,
        bool completed,
        bool pending,
        bool notSubmitted,
        bool cancelled,
        bool failure,
        bool terminal)
    {
        Assert.Equal(completed, LinklyBackendSessionClassifier.IsCompleted(status));
        Assert.Equal(pending, LinklyBackendSessionClassifier.IsPending(status));
        Assert.Equal(notSubmitted, LinklyBackendSessionClassifier.IsNotSubmitted(status));
        Assert.Equal(cancelled, LinklyBackendSessionClassifier.IsCancelled(status));
        Assert.Equal(failure, LinklyBackendSessionClassifier.IsFailureStatus(status));
        Assert.Equal(terminal, LinklyBackendSessionClassifier.IsTerminalStatusName(status));
    }

    [Fact]
    public void Failure_status_set_stays_in_sync_with_the_settlement_failure_set_in_contracts()
    {
        // 会话失败类状态名与结算会话的失败集合是同一个集合（Contracts 里一份、客户端一份），两处必须同步演进。
        foreach (var status in AllStatuses)
        {
            Assert.Equal(
                LinklyCloudBackendStatusConstants.IsSettlementFailureStatus(status),
                LinklyBackendSessionClassifier.IsFailureStatus(status));
        }
    }

    // ------------------------------------------------------------------
    // 状态名 + 成败位
    // ------------------------------------------------------------------

    public static IEnumerable<object?[]> SessionOutcomeRows()
    {
        foreach (var status in AllStatuses)
        {
            foreach (var flag in AllSuccessFlags)
            {
                var isCompleted = string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase);
                var isFailureName = status is not null &&
                    (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "NotSubmitted", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase));
                // 期望值逐类显式给出，不复用被测实现。
                var approved = isCompleted && flag == true;
                var declined = (isCompleted && flag == false) || isFailureName;
                var withoutFlag = isCompleted && flag is null;
                yield return [status, flag, approved, declined, withoutFlag];
            }
        }
    }

    [Theory]
    [MemberData(nameof(SessionOutcomeRows))]
    public void Session_outcome_predicates(
        string? status,
        bool? transactionSuccess,
        bool expectedApproved,
        bool expectedDeclinedOrFailed,
        bool expectedWithoutFlag)
    {
        var session = Session(status, transactionSuccess);

        Assert.Equal(expectedApproved, LinklyBackendSessionClassifier.IsApprovedByFlag(session));
        Assert.Equal(expectedDeclinedOrFailed, LinklyBackendSessionClassifier.IsDeclinedOrFailed(session));
        Assert.Equal(expectedWithoutFlag, LinklyBackendSessionClassifier.IsCompletedWithoutSuccessFlag(session));
    }

    [Fact]
    public void Session_outcome_predicates_keep_their_invariants()
    {
        foreach (var status in AllStatuses)
        {
            foreach (var flag in AllSuccessFlags)
            {
                var session = Session(status, flag, responseCode: "00");
                var approved = LinklyBackendSessionClassifier.IsApprovedByFlag(session);
                var declined = LinklyBackendSessionClassifier.IsDeclinedOrFailed(session);
                var withoutFlag = LinklyBackendSessionClassifier.IsCompletedWithoutSuccessFlag(session);
                var terminal = LinklyBackendSessionClassifier.IsTerminalStatusName(status);
                var label = $"{status ?? "<null>"}/{flag?.ToString() ?? "null"}";

                // 批准与未批准互斥；三者之一成立必为状态名终态；Completed 缺成败位是"终态但无结论"。
                Assert.False(approved && declined, label);
                Assert.False(approved && withoutFlag, label);
                Assert.False(declined && withoutFlag, label);
                Assert.True(!approved || terminal, label);
                Assert.True(!declined || terminal, label);
                Assert.True(!withoutFlag || terminal, label);
                Assert.True(terminal == (approved || declined || withoutFlag), label);

                // "明确批准"比实时收款口径的批准更保守：前者成立则后者必成立。
                Assert.True(!approved || LinklyBackendTerminalClient.IsApprovedFinalTransaction(session), label);
            }
        }
    }

    // ------------------------------------------------------------------
    // 失败归类：只看 "Status ResponseText"，优先级 TIMEOUT > CANCEL > DECLIN > Failed
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Failed", null, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("NotSubmitted", null, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Cancelled", null, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Canceled", null, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("cancelled", null, LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Completed", null, LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Completed", "DECLINED", LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("Completed", "declined by issuer", LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("Completed", "SYSTEM ERROR", LocalCardPaymentAttemptStatus.Failed)]
    [InlineData("Completed", "TIMEOUT", LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("Failed", "Transaction timeout", LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("Failed", "timeout then cancelled", LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("Failed", "cancel or declined", LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Failed", "OPERATOR CANCELLED", LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("Failed", "Not cancelled", LocalCardPaymentAttemptStatus.Cancelled)]     // 子串匹配：否定句也命中，如实记录
    [InlineData("Pending", null, LocalCardPaymentAttemptStatus.Failed)]                  // 调用方须保证不是批准/在途；这里只是文本归类
    [InlineData("Weird", "DECLINED", LocalCardPaymentAttemptStatus.Declined)]
    [InlineData("", null, LocalCardPaymentAttemptStatus.Failed)]
    public void Failure_outcome_mapping(
        string status,
        string? responseText,
        LocalCardPaymentAttemptStatus expected)
    {
        var session = Session(status, responseText: responseText);

        Assert.Equal(expected, LinklyBackendSessionClassifier.MapFailureOutcome(session));
    }

    [Fact]
    public void Failure_outcome_mapping_ignores_the_response_code()
    {
        // TO / TM / 05 等响应码不参与（与按响应码归类的本地路径不同）。
        foreach (var code in new[] { "TO", "TM", "05", "C0", "CA", "00" })
        {
            Assert.Equal(
                LocalCardPaymentAttemptStatus.Failed,
                LinklyBackendSessionClassifier.MapFailureOutcome(Session("Failed", responseCode: code)));
        }
    }

    // ------------------------------------------------------------------
    // 批准金额核验
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(4000L, "40.00", true)]
    [InlineData(4001L, "40.00", false)]
    [InlineData(3999L, "40.00", false)]
    [InlineData(null, "40.00", false)]
    [InlineData(4000L, "-40.00", true)]
    [InlineData(-4000L, "40.00", false)]
    [InlineData(4000L, "40.004", true)]
    [InlineData(4000L, "40.005", false)]
    public void TryReadVerifiedApproval_card_transaction_boundaries(
        long? cardAmountCents,
        string expectedAmountText,
        bool expected)
    {
        var session = Session("Completed", true, "00", "APPROVED", withCard: true, cardAmountCents: cardAmountCents);

        var verified = LinklyBackendSessionClassifier.TryReadVerifiedApproval(
            session,
            decimal.Parse(expectedAmountText, CultureInfo.InvariantCulture),
            TxnRef,
            out var result);

        Assert.Equal(expected, verified);
        // 无论核验是否通过都带回读到的交易结果。
        Assert.Equal(session.SessionId, result.SessionId);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("4000", "40.00", true)]
    [InlineData("4001", "40.00", false)]
    [InlineData("3999", "40.00", false)]
    [InlineData("4000", "43.00", false)]   // 请求总额含小费：AmtTip 不会被加到 AmtPurchase 上
    [InlineData("4000", "60.00", false)]   // 请求总额含 cash-out：AmtCash 不会被加上
    [InlineData("4000", "40.40", false)]   // 请求总额含附加费：AmtSurcharge 不会被加上
    public void TryReadVerifiedApproval_notification_boundaries_only_use_amt_purchase(
        string amountMinor,
        string expectedAmountText,
        bool expected)
    {
        // 通知里同时带 AmtTip=300 / AmtCash=2000 / AmtSurcharge=40：只有 AmtPurchase 参与核验。
        var session = Session("Completed", true, "00", "APPROVED", notificationAmountMinor: amountMinor);

        Assert.Equal(
            expected,
            LinklyBackendSessionClassifier.TryReadVerifiedApproval(
                session,
                decimal.Parse(expectedAmountText, CultureInfo.InvariantCulture),
                TxnRef,
                out _));
    }

    [Fact]
    public void TryReadVerifiedApproval_requires_the_terminal_to_have_approved()
    {
        // 成败位 false：即使金额完全一致也不是批准证据。
        var session = Session("Completed", false, "05", "DECLINED", withCard: true, cardAmountCents: 4000);

        Assert.False(LinklyBackendSessionClassifier.TryReadVerifiedApproval(session, 40m, TxnRef, out var result));
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SOMETHING-ELSE")]
    public void TryReadVerifiedApproval_does_not_compare_the_expected_txn_ref(string? expectedTxnRef)
    {
        var session = Session("Completed", true, "00", "APPROVED", withCard: true, cardAmountCents: 4000);

        Assert.True(LinklyBackendSessionClassifier.TryReadVerifiedApproval(session, 40m, expectedTxnRef, out _));
    }

    [Theory]
    [InlineData("Completed", 4000L, true)]
    [InlineData("Completed", 4001L, false)]
    [InlineData("Failed", 4000L, true)]      // 终态核验只看证据本身，不再看状态名（调用方已按状态名分流）
    public void HasFinalApprovalEvidenceMatchingAttempt_delegates_to_the_shared_verification(
        string status,
        long cardAmountCents,
        bool expected)
    {
        var session = Session(status, true, "00", "APPROVED", withCard: true, cardAmountCents: cardAmountCents);

        Assert.Equal(expected, LinklyBackendTerminalClient.HasFinalApprovalEvidenceMatchingAttempt(session, TxnRef, 40m));
        Assert.Equal(
            expected,
            LinklyBackendSessionClassifier.TryReadVerifiedApproval(session, 40m, TxnRef, out _));
    }
}
