using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖刷卡结果策略：终端原始 StatusKey/Message 如何收敛成付款页的
/// 终态（取消/超时/结果未知）与错误类型。RequiresRecovery 决定是否加恢复锁，
/// 误判会导致重复扣款或已扣款却放行，因此逐个 provider 固定映射。
/// </summary>
public sealed class CardPaymentResultPolicyTests
{
    // ── Resolver ──

    [Fact]
    public void Resolver_returns_successful_result_unchanged_without_consulting_policies()
    {
        var policy = new RecordingPolicy(canClassify: true, new(CardPaymentTerminalOutcome.ResultUnknown));
        var resolver = new CardPaymentResultPolicyResolver([policy]);
        var result = PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "ANZ:TXN-1"),
            "payment.status.cardApproved");

        var applied = resolver.Apply(result);

        Assert.Same(result, applied);
        Assert.Null(applied.CardResult);
        Assert.Equal(0, policy.CanClassifyCalls);
    }

    [Fact]
    public void Resolver_keeps_disposition_already_set_by_workflow()
    {
        var policy = new RecordingPolicy(canClassify: true, new(CardPaymentTerminalOutcome.Cancelled));
        var resolver = new CardPaymentResultPolicyResolver([policy]);
        var existing = new CardPaymentResultDisposition(
            CardPaymentTerminalOutcome.ResultUnknown,
            CardPaymentErrorKind.ActiveSessionRequiresRecovery,
            PreserveStatus: true);
        var result = PaymentTenderAttemptResult.Fail("linkly.local.timeout") with { CardResult = existing };

        var applied = resolver.Apply(result);

        Assert.Same(result, applied);
        Assert.Same(existing, applied.CardResult);
        Assert.Equal(0, policy.CanClassifyCalls);
    }

    [Fact]
    public void Resolver_uses_first_policy_that_can_classify_and_preserves_other_fields()
    {
        var skipped = new RecordingPolicy(canClassify: false, new(CardPaymentTerminalOutcome.Cancelled));
        var chosen = new RecordingPolicy(canClassify: true, new(CardPaymentTerminalOutcome.TimedOut, CardPaymentErrorKind.Timeout));
        var neverReached = new RecordingPolicy(canClassify: true, new(CardPaymentTerminalOutcome.ResultUnknown));
        var resolver = new CardPaymentResultPolicyResolver([skipped, chosen, neverReached]);
        var recoveryKey = new CardRecoveryAttemptKey(CardProcessorKind.Linkly, Guid.NewGuid());
        var orderGuid = Guid.NewGuid();
        var result = PaymentTenderAttemptResult.Fail("custom.key", "custom message", isTerminalDecline: true) with
        {
            RecoveryAttemptKey = recoveryKey,
            RecoveryOrderGuid = orderGuid
        };

        var applied = resolver.Apply(result);

        Assert.Equal(CardPaymentTerminalOutcome.TimedOut, applied.CardResult!.Outcome);
        Assert.Equal(CardPaymentErrorKind.Timeout, applied.CardResult.ErrorKind);
        Assert.Equal(1, skipped.CanClassifyCalls);
        Assert.Equal(0, skipped.ClassifyCalls);
        Assert.Equal(1, chosen.ClassifyCalls);
        Assert.Equal(0, neverReached.CanClassifyCalls);
        Assert.False(applied.Succeeded);
        Assert.Equal("custom.key", applied.StatusKey);
        Assert.Equal("custom message", applied.StatusMessage);
        Assert.True(applied.IsTerminalDecline);
        Assert.Equal(recoveryKey, applied.RecoveryAttemptKey);
        Assert.Equal(orderGuid, applied.RecoveryOrderGuid);
    }

    [Fact]
    public void Resolver_without_matching_policy_sets_explicit_none_disposition()
    {
        var resolver = new CardPaymentResultPolicyResolver([new RecordingPolicy(canClassify: false, new(CardPaymentTerminalOutcome.Cancelled))]);

        var applied = resolver.Apply(PaymentTenderAttemptResult.Fail("payment.card.resultUnknown"));

        Assert.Same(CardPaymentResultDisposition.None, applied.CardResult);
        Assert.False(applied.CardResult!.RequiresRecovery);
    }

    [Fact]
    public void Production_policy_order_lets_linkly_status_key_win_over_fallback_message_heuristics()
    {
        // 与 ServiceRegistration / CashPaymentWorkflowService 的默认顺序一致：Linkly → Square → Fallback。
        var resolver = CreateProductionResolver();

        // 消息里带 "cancel"，Fallback 会判为取消；但 Linkly 已认领该 key，必须按超时处理。
        var applied = resolver.Apply(PaymentTenderAttemptResult.Fail(
            "linkly.local.timeout",
            "Terminal timed out after the operator cancelled the prompt."));

        Assert.Equal(CardPaymentTerminalOutcome.TimedOut, applied.CardResult!.Outcome);
        Assert.Equal(CardPaymentErrorKind.Timeout, applied.CardResult.ErrorKind);
    }

    [Fact]
    public void Production_policy_order_routes_unknown_linkly_key_to_linkly_none_instead_of_fallback()
    {
        var resolver = CreateProductionResolver();

        // Linkly 认领后不再回落到 Fallback 的消息推断；未知 linkly.* key 不得被消息里的 "could not be confirmed" 升级为恢复锁。
        var applied = resolver.Apply(PaymentTenderAttemptResult.Fail(
            "linkly.local.declined",
            "Result could not be confirmed"));

        Assert.Same(CardPaymentResultDisposition.None, applied.CardResult);
    }

    [Fact]
    public void Production_policy_order_sends_generic_status_to_fallback()
    {
        var resolver = CreateProductionResolver();

        var applied = resolver.Apply(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "The card payment could not be confirmed."));

        Assert.True(applied.CardResult!.RequiresRecovery);
        Assert.Equal(CardPaymentErrorKind.ActiveSessionRequiresRecovery, applied.CardResult.ErrorKind);
    }

    [Fact]
    public void Production_policy_order_maps_missing_linkly_adapter_by_english_message()
    {
        // CardTerminalService 在未配置 Linkly 适配器时不带 StatusKey，工作流回落为 cardDeclined，
        // 只能依靠 Fallback 的英文消息推断出连接失败。
        var resolver = CreateProductionResolver();

        var applied = resolver.Apply(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "ANZ Linkly terminal adapter is unavailable."));

        Assert.Equal(CardPaymentErrorKind.ConnectionFailed, applied.CardResult!.ErrorKind);
        Assert.False(applied.CardResult.RequiresRecovery);
    }

    // ── Linkly ──

    [Theory]
    [InlineData("linkly.local.timeout", true)]
    [InlineData("linkly.backend.anything", true)]
    [InlineData("payment.card.resultUnknown", true)]
    [InlineData("Linkly.local.timeout", false)]
    [InlineData("payment.card.squareTimedOut", false)]
    [InlineData("payment.status.cardDeclined", false)]
    [InlineData("", false)]
    public void Linkly_policy_claims_only_linkly_prefixed_keys_and_generic_result_unknown(string statusKey, bool expected)
    {
        var policy = new LinklyCardPaymentResultPolicy();

        Assert.Equal(expected, policy.CanClassify(PaymentTenderAttemptResult.Fail(statusKey)));
    }

    [Theory]
    [InlineData("payment.card.resultUnknown")]
    [InlineData("linkly.backend.resultUnknown")]
    [InlineData("linkly.backend.cancelledUnknown")]
    [InlineData("linkly.cloud.resultUnknown")]
    public void Linkly_policy_marks_unknown_results_for_recovery_and_preserves_status(string statusKey)
    {
        var disposition = new LinklyCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(statusKey));

        Assert.Equal(CardPaymentTerminalOutcome.ResultUnknown, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.ActiveSessionRequiresRecovery, disposition.ErrorKind);
        Assert.True(disposition.PreserveStatus);
        Assert.True(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("linkly.local.connectionFailed", CardPaymentErrorKind.ConnectionFailed)]
    [InlineData("payment.card.linklyUnavailable", CardPaymentErrorKind.ConnectionFailed)]
    [InlineData("linkly.cloud.communicationFailed", CardPaymentErrorKind.CloudCommunicationFailed)]
    [InlineData("linkly.backend.communicationFailed", CardPaymentErrorKind.CloudCommunicationFailed)]
    public void Linkly_policy_maps_transport_failures_without_recovery_lock(string statusKey, CardPaymentErrorKind expectedKind)
    {
        var disposition = new LinklyCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(statusKey));

        Assert.Equal(CardPaymentTerminalOutcome.None, disposition.Outcome);
        Assert.Equal(expectedKind, disposition.ErrorKind);
        Assert.False(disposition.PreserveStatus);
        Assert.False(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("linkly.local.timeout")]
    [InlineData("linkly.cloud.timeout")]
    [InlineData("linkly.backend.timeout")]
    public void Linkly_policy_maps_timeouts_to_timed_out_without_recovery_lock(string statusKey)
    {
        var disposition = new LinklyCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(statusKey));

        Assert.Equal(CardPaymentTerminalOutcome.TimedOut, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.Timeout, disposition.ErrorKind);
        Assert.False(disposition.PreserveStatus);
        Assert.False(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("linkly.local.declined")]
    [InlineData("linkly.backend.declined")]
    [InlineData("linkly.cloud.invalidResponse")]
    [InlineData("linkly.cloud.notPaired")]
    public void Linkly_policy_returns_none_for_other_linkly_keys(string statusKey)
    {
        var disposition = new LinklyCardPaymentResultPolicy().Classify(
            PaymentTenderAttemptResult.Fail(statusKey, "Result could not be confirmed; timed out; cancelled"));

        Assert.Same(CardPaymentResultDisposition.None, disposition);
    }

    [Fact]
    public void Linkly_active_session_rejection_does_not_create_new_recovery_lock()
    {
        // 旧 session 未释放时新交易尚未提交（LinklyBackendTerminalClient 注释），不能被策略升级成 ResultUnknown 锁。
        var disposition = new LinklyCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(
            "linkly.backend.activeSessionRequiresRecovery",
            "Current terminal already has an unfinished card transaction."));

        Assert.False(disposition.RequiresRecovery);
        Assert.NotEqual(CardPaymentTerminalOutcome.ResultUnknown, disposition.Outcome);
    }

    // ── Square ──

    [Theory]
    [InlineData("payment.card.square", true)]
    [InlineData("payment.card.squareTimedOut", true)]
    [InlineData("payment.card.squareAnything", true)]
    [InlineData("payment.card.Square", false)]
    [InlineData("linkly.local.timeout", false)]
    [InlineData("payment.card.resultUnknown", false)]
    public void Square_policy_claims_only_square_prefixed_keys(string statusKey, bool expected)
    {
        Assert.Equal(expected, new SquareCardPaymentResultPolicy().CanClassify(PaymentTenderAttemptResult.Fail(statusKey)));
    }

    [Theory]
    [InlineData("payment.card.squareCanceled")]
    [InlineData("payment.card.squareCanceledBuyer")]
    [InlineData("payment.card.squareCanceledSeller")]
    public void Square_policy_maps_all_cancel_variants_to_cancelled_and_preserves_status(string statusKey)
    {
        var disposition = new SquareCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(statusKey));

        Assert.Equal(CardPaymentTerminalOutcome.Cancelled, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.None, disposition.ErrorKind);
        Assert.True(disposition.PreserveStatus);
        Assert.False(disposition.RequiresRecovery);
    }

    [Fact]
    public void Square_policy_maps_timeout_and_preserves_square_specific_status()
    {
        var disposition = new SquareCardPaymentResultPolicy().Classify(
            PaymentTenderAttemptResult.Fail("payment.card.squareTimedOut"));

        Assert.Equal(CardPaymentTerminalOutcome.TimedOut, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.Timeout, disposition.ErrorKind);
        Assert.True(disposition.PreserveStatus);
        Assert.False(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("payment.card.squareTerminalOffline")]
    [InlineData("payment.card.squareTerminalNotPickedUp")]
    [InlineData("payment.card.squareCommunicationFailed")]
    public void Square_policy_maps_device_and_network_failures_to_square_communication_failed(string statusKey)
    {
        var disposition = new SquareCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(statusKey));

        Assert.Equal(CardPaymentTerminalOutcome.None, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.SquareCommunicationFailed, disposition.ErrorKind);
        Assert.True(disposition.PreserveStatus);
        Assert.False(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("payment.card.squarePaymentAmountMismatch")]
    [InlineData("payment.card.squareInvalidResponse")]
    [InlineData("payment.card.squareRefundPending")]
    public void Square_policy_returns_none_for_other_square_keys(string statusKey)
    {
        var disposition = new SquareCardPaymentResultPolicy().Classify(
            PaymentTenderAttemptResult.Fail(statusKey, "Square communication failed and timed out"));

        Assert.Same(CardPaymentResultDisposition.None, disposition);
    }

    // ── Fallback ──

    [Fact]
    public void Fallback_policy_claims_every_result()
    {
        var policy = new FallbackCardPaymentResultPolicy();

        Assert.True(policy.CanClassify(PaymentTenderAttemptResult.Fail(string.Empty)));
        Assert.True(policy.CanClassify(PaymentTenderAttemptResult.Fail("linkly.local.timeout")));
    }

    [Theory]
    [InlineData("payment.card.resultUnknown", null)]
    [InlineData("payment.status.cardDeclined", "Payment could not be confirmed.")]
    [InlineData("other", "RESULT COULD NOT BE CONFIRMED")]
    public void Fallback_policy_marks_unconfirmed_results_for_recovery(string statusKey, string? message)
    {
        var disposition = new FallbackCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(statusKey, message));

        Assert.Equal(CardPaymentTerminalOutcome.ResultUnknown, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.ActiveSessionRequiresRecovery, disposition.ErrorKind);
        Assert.True(disposition.PreserveStatus);
        Assert.True(disposition.RequiresRecovery);
    }

    [Fact]
    public void Fallback_policy_treats_unconfirmed_cancellation_as_unknown_not_cancelled()
    {
        // "could not be confirmed" 优先于 "cancel"：取消结果未确认时仍可能已扣款，必须保留恢复锁。
        var disposition = new FallbackCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "The cancellation outcome could not be confirmed."));

        Assert.Equal(CardPaymentTerminalOutcome.ResultUnknown, disposition.Outcome);
        Assert.True(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("payment.status.cardCancelled", null)]
    [InlineData("other", "Operator CANCELLED the payment")]
    [InlineData("other", "Transaction was canceled by customer")]
    public void Fallback_policy_maps_cancellation(string statusKey, string? message)
    {
        var disposition = new FallbackCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(statusKey, message));

        Assert.Equal(CardPaymentTerminalOutcome.Cancelled, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.None, disposition.ErrorKind);
        Assert.False(disposition.PreserveStatus);
        Assert.False(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("payment.status.cardTimedOut", null)]
    [InlineData("other", "Terminal TIMED OUT")]
    [InlineData("other", "Gateway timeout")]
    public void Fallback_policy_maps_timeouts(string statusKey, string? message)
    {
        var disposition = new FallbackCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(statusKey, message));

        Assert.Equal(CardPaymentTerminalOutcome.TimedOut, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.Timeout, disposition.ErrorKind);
        Assert.False(disposition.RequiresRecovery);
    }

    [Fact]
    public void Fallback_policy_maps_unfinished_terminal_transaction_to_recovery_without_preserving_status()
    {
        var disposition = new FallbackCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "Current terminal already has an Unfinished Card Transaction."));

        Assert.Equal(CardPaymentTerminalOutcome.ResultUnknown, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.ActiveSessionRequiresRecovery, disposition.ErrorKind);
        Assert.False(disposition.PreserveStatus);
        Assert.True(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("Terminal connection failed")]
    [InlineData("The connection was closed by the terminal")]
    [InlineData("Request could not be sent")]
    [InlineData("Card terminal UNAVAILABLE")]
    public void Fallback_policy_maps_connection_messages_to_connection_failed(string message)
    {
        var disposition = new FallbackCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail("other", message));

        Assert.Equal(CardPaymentTerminalOutcome.None, disposition.Outcome);
        Assert.Equal(CardPaymentErrorKind.ConnectionFailed, disposition.ErrorKind);
        Assert.False(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData("Square communication failed", CardPaymentErrorKind.SquareCommunicationFailed)]
    [InlineData("square COMMUNICATION FAILED", CardPaymentErrorKind.SquareCommunicationFailed)]
    [InlineData("Cloud communication failed", CardPaymentErrorKind.CloudCommunicationFailed)]
    public void Fallback_policy_distinguishes_square_and_cloud_communication_failures(
        string message,
        CardPaymentErrorKind expectedKind)
    {
        var disposition = new FallbackCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail("other", message));

        Assert.Equal(CardPaymentTerminalOutcome.None, disposition.Outcome);
        Assert.Equal(expectedKind, disposition.ErrorKind);
    }

    [Fact]
    public void Fallback_policy_requires_both_decline_key_and_terminal_evidence_for_card_declined()
    {
        var policy = new FallbackCardPaymentResultPolicy();

        var terminalDecline = policy.Classify(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined", "Insufficient funds", isTerminalDecline: true));
        var declineWithoutEvidence = policy.Classify(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined", "Insufficient funds", isTerminalDecline: false));
        var evidenceWithOtherKey = policy.Classify(PaymentTenderAttemptResult.Fail(
            "payment.status.voucherDeclined", "Insufficient funds", isTerminalDecline: true));

        Assert.Equal(CardPaymentErrorKind.CardDeclined, terminalDecline.ErrorKind);
        Assert.Equal(CardPaymentTerminalOutcome.None, terminalDecline.Outcome);
        Assert.Same(CardPaymentResultDisposition.None, declineWithoutEvidence);
        Assert.Same(CardPaymentResultDisposition.None, evidenceWithOtherKey);
    }

    [Fact]
    public void Fallback_policy_returns_none_for_unrecognised_failure_and_null_message()
    {
        var disposition = new FallbackCardPaymentResultPolicy().Classify(PaymentTenderAttemptResult.Fail("other", null));

        Assert.Same(CardPaymentResultDisposition.None, disposition);
        Assert.False(disposition.RequiresRecovery);
    }

    [Theory]
    [InlineData(CardPaymentTerminalOutcome.None, false)]
    [InlineData(CardPaymentTerminalOutcome.Cancelled, false)]
    [InlineData(CardPaymentTerminalOutcome.TimedOut, false)]
    [InlineData(CardPaymentTerminalOutcome.ResultUnknown, true)]
    public void Disposition_requires_recovery_only_for_unknown_result(CardPaymentTerminalOutcome outcome, bool expected)
    {
        Assert.Equal(expected, new CardPaymentResultDisposition(outcome).RequiresRecovery);
    }

    private static CardPaymentResultPolicyResolver CreateProductionResolver() =>
        new(
        [
            new LinklyCardPaymentResultPolicy(),
            new SquareCardPaymentResultPolicy(),
            new FallbackCardPaymentResultPolicy()
        ]);

    private sealed class RecordingPolicy(bool canClassify, CardPaymentResultDisposition disposition) : ICardPaymentResultPolicy
    {
        public int CanClassifyCalls { get; private set; }

        public int ClassifyCalls { get; private set; }

        public bool CanClassify(PaymentTenderAttemptResult result)
        {
            CanClassifyCalls++;
            return canClassify;
        }

        public CardPaymentResultDisposition Classify(PaymentTenderAttemptResult result)
        {
            ClassifyCalls++;
            return disposition;
        }
    }
}
