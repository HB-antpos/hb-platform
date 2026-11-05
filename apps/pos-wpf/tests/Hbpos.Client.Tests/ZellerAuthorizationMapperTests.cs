using Hbpos.Client.Wpf.Services.Zeller;

namespace Hbpos.Client.Tests;

/// <summary>
/// Zeller 结论到现有 PaymentAuthorizationResult 的映射：批准时的订单引用和刷卡证据、
/// 拒付时付款页需要的银行响应码、结果未知时的 ResultUnknown 标记。
/// </summary>
public sealed class ZellerAuthorizationMapperTests
{
    private const string Reference = "HBZ-799287a33dc24df1bca3d33eea19b35a";
    private const string Uuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";
    private static readonly Guid Attempt = Guid.Parse("799287a3-3dc2-4df1-bca3-d33eea19b35a");
    private static readonly DateTimeOffset BankTime = new(2026, 11, 5, 11, 21, 10, TimeSpan.FromHours(10));

    private static readonly ZellerTransaction ApprovedTransaction = new(
        ZellerTransactionStatus.Approved,
        ZellerTransactionType.Purchase,
        100,
        Uuid,
        Reference,
        ResponseCode: "00",
        ResponseText: "APPROVED",
        ApprovalCode: "456321",
        Rrn: "264753970917",
        PanMasked: "…2902",
        Scheme: "VISA",
        Bin: "483028",
        MerchantId: "000000000143281",
        TerminalId: "00134553",
        ReceiptText: "RECEIPT",
        Timestamp: BankTime);

    [Fact]
    public void Approved_outcome_carries_the_order_reference_and_card_evidence()
    {
        var result = ZellerAuthorizationMapper.ToAuthorizationResult(
            new ZellerPaymentOutcome(ZellerPaymentState.Approved, Attempt, Reference, 100, Transaction: ApprovedTransaction));

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal($"ZELLER:{Reference}:{Uuid}", result.Reference);
        Assert.Equal(1.00m, result.AuthorizedAmount);
        Assert.Equal("Zeller", result.Processor);
        Assert.Equal(Reference, result.TxnRef);
        Assert.Equal("payment.card.zeller.approved", result.StatusKey);
        var card = Assert.Single(result.CardTransactions!);
        Assert.Equal("Zeller", card.Processor);
        Assert.Equal(Uuid, card.TxnRef);
        Assert.Equal("456321", card.AuthCode);
        Assert.Equal("VISA", card.CardType);
        Assert.Equal(483028, card.CardBin);
        Assert.Equal("…2902", card.MaskedCardNumber);
        Assert.Equal("000000000143281", card.MerchantId);
        Assert.Equal("00", card.ResponseCode);
        Assert.Equal(BankTime, card.BankDateTime);
        Assert.Equal(1.00m, card.Amount);
        Assert.Equal("RECEIPT", card.ReceiptText);
        Assert.Null(card.Stan);
    }

    [Fact]
    public void Approval_recovered_by_a_query_has_no_receipt_or_approval_code()
    {
        var queried = ApprovedTransaction with { ApprovalCode = "", ReceiptText = null, Bin = "not-a-number" };

        var card = ZellerAuthorizationMapper.ToCardTransaction(queried);

        Assert.Null(card.AuthCode);
        Assert.Null(card.ReceiptText);
        Assert.Null(card.CardBin);
    }

    [Fact]
    public void Approved_outcome_without_its_transaction_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => ZellerAuthorizationMapper.ToAuthorizationResult(
            new ZellerPaymentOutcome(ZellerPaymentState.Approved, Attempt, Reference, 100)));
    }

    [Fact]
    public void Declined_outcome_carries_the_bank_response_so_the_decline_prompt_appears()
    {
        var declined = ApprovedTransaction with { Status = ZellerTransactionStatus.Declined, ResponseCode = "51", ResponseText = "Insufficient funds" };

        var result = ZellerAuthorizationMapper.ToAuthorizationResult(
            new ZellerPaymentOutcome(ZellerPaymentState.Declined, Attempt, Reference, 100, ZellerFailureKind.BankDeclined, Transaction: declined));

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("51", result.ResponseCode);
        Assert.Equal("Insufficient funds", result.ResponseText);
        Assert.Equal("payment.card.zeller.declined", result.StatusKey);
        Assert.Null(result.CardTransactions);
    }

    [Fact]
    public void Declined_outcome_without_a_transaction_still_reports_a_bank_decline()
    {
        var result = ZellerAuthorizationMapper.ToAuthorizationResult(
            new ZellerPaymentOutcome(ZellerPaymentState.Declined, Attempt, Reference, 100, ZellerFailureKind.CardProblem));

        Assert.False(string.IsNullOrWhiteSpace(result.ResponseCode));
        Assert.Equal("payment.card.zeller.cardProblem", result.StatusKey);
    }

    [Theory]
    [InlineData(ZellerFailureKind.Cancelled, "cancelled")]
    [InlineData(ZellerFailureKind.Timeout, "timeout")]
    [InlineData(ZellerFailureKind.TerminalUnavailable, "terminalUnavailable")]
    [InlineData(ZellerFailureKind.SetupRequired, "setupRequired")]
    [InlineData(ZellerFailureKind.SdkUnavailable, "sdkUnavailable")]
    [InlineData(ZellerFailureKind.Busy, "busy")]
    [InlineData(ZellerFailureKind.InvalidRequest, "invalidRequest")]
    [InlineData(ZellerFailureKind.None, "notPaid")]
    public void Not_paid_outcomes_map_to_a_status_key_per_reason(ZellerFailureKind kind, string key)
    {
        var result = ZellerAuthorizationMapper.ToAuthorizationResult(
            new ZellerPaymentOutcome(ZellerPaymentState.NotPaid, Attempt, Reference, 100, kind));

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("payment.card.zeller." + key, result.StatusKey);
        Assert.Null(result.ResponseCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void Approved_refund_returns_the_refund_reference_for_the_workflow_to_wrap()
    {
        const string refundReference = "HBZR-799287a33dc24df1bca3d33eea19b35a";
        const string refundUuid = "28b1e54e-a420-41d6-bb8e-2a7497bf783d";
        var refund = ApprovedTransaction with { Type = ZellerTransactionType.Refund, TransactionUuid = refundUuid, ExternalReference = refundReference };

        var result = ZellerAuthorizationMapper.ToAuthorizationResult(new ZellerPaymentOutcome(
            ZellerPaymentState.Approved, Attempt, refundReference, 100, Transaction: refund, Operation: ZellerAttemptOperation.Refund));

        Assert.True(result.Approved);
        Assert.Equal($"ZELLERRF:{refundReference}:{refundUuid}", result.Reference);
        Assert.Equal("payment.card.zeller.refundApproved", result.StatusKey);
        var card = Assert.Single(result.CardTransactions!);
        Assert.Equal(refundUuid, card.TxnRef);
        Assert.Equal(1.00m, card.Amount);
        Assert.Null(card.RefundReference);
    }

    [Fact]
    public void Declined_refund_has_its_own_status_key()
    {
        var result = ZellerAuthorizationMapper.ToAuthorizationResult(new ZellerPaymentOutcome(
            ZellerPaymentState.Declined,
            Attempt,
            "HBZR-799287a33dc24df1bca3d33eea19b35a",
            100,
            ZellerFailureKind.BankDeclined,
            Transaction: ApprovedTransaction with { Type = ZellerTransactionType.Refund, Status = ZellerTransactionStatus.Declined, ResponseCode = "13", ResponseText = "Invalid amount" },
            Operation: ZellerAttemptOperation.Refund));

        Assert.Equal("payment.card.zeller.refundDeclined", result.StatusKey);
        Assert.Equal("13", result.ResponseCode);
    }

    [Fact]
    public void Refund_of_a_missing_original_has_its_own_status_key()
    {
        var result = ZellerAuthorizationMapper.ToAuthorizationResult(new ZellerPaymentOutcome(
            ZellerPaymentState.NotPaid, Attempt, "HBZR-799287a33dc24df1bca3d33eea19b35a", 100,
            ZellerFailureKind.OriginalTransactionNotFound, Operation: ZellerAttemptOperation.Refund));

        Assert.Equal("payment.card.zeller.originalNotFound", result.StatusKey);
        Assert.Contains("Nothing was refunded", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refund_already_in_progress_is_unknown_and_says_not_to_refund_again()
    {
        var result = ZellerAuthorizationMapper.ToAuthorizationResult(new ZellerPaymentOutcome(
            ZellerPaymentState.Unknown, Attempt, "HBZR-799287a33dc24df1bca3d33eea19b35a", 100,
            ZellerFailureKind.ResultUnknown, ZellerUnknownReason.RefundAlreadyInProgress, Operation: ZellerAttemptOperation.Refund));

        Assert.True(result.ResultUnknown);
        Assert.Equal("payment.card.zeller.refundInProgress", result.StatusKey);
        Assert.Contains("already in progress", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_refund_says_not_to_refund_again()
    {
        var result = ZellerAuthorizationMapper.ToAuthorizationResult(new ZellerPaymentOutcome(
            ZellerPaymentState.Unknown, Attempt, "HBZR-799287a33dc24df1bca3d33eea19b35a", 100,
            ZellerFailureKind.ResultUnknown, ZellerUnknownReason.SdkError, Operation: ZellerAttemptOperation.Refund));

        Assert.True(result.ResultUnknown);
        Assert.Equal("payment.card.zeller.resultUnknown", result.StatusKey);
        Assert.Contains("Do not refund again", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_outcome_is_flagged_so_the_payment_page_locks_for_recovery()
    {
        var result = ZellerAuthorizationMapper.ToAuthorizationResult(
            new ZellerPaymentOutcome(ZellerPaymentState.Unknown, Attempt, Reference, 1250, ZellerFailureKind.ResultUnknown, ZellerUnknownReason.SdkError));

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("payment.card.zeller.resultUnknown", result.StatusKey);
        Assert.Equal(12.50m, result.AuthorizedAmount);
        Assert.Equal(Reference, result.TxnRef);
        Assert.Contains("Do not ask the customer to pay again", result.Message, StringComparison.Ordinal);
    }
}
