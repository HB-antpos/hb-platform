using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖 Linkly 本地直连恢复的交易身份核对：恢复查询返回的结果只有在交易类型、
/// 所有返回的 TxnRef（含 ANZ: 前缀 / 协议空格填充）以及金额都与本地 attempt 一致时，
/// 才能被当作同一笔交易落单，否则可能把另一笔交易的批准结果记到当前订单上。
/// </summary>
public sealed class LinklyLocalTransactionIdentityTests
{
    private const string TxnRef = "HB1234567890ABCD";

    [Theory]
    [InlineData("P")]
    [InlineData("R")]
    public void Approved_result_with_same_type_reference_and_amount_matches(string txnType)
    {
        var result = Approved(txnType, txnRef: TxnRef, amount: 25.50m);

        Assert.True(LinklyLocalTransactionIdentity.Matches(TxnRef, txnType, 25.50m, result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("p")]
    [InlineData("S")]
    [InlineData("PR")]
    public void Unsupported_expected_transaction_type_never_matches(string? txnType)
    {
        var result = Approved(txnType, txnRef: TxnRef, amount: 10m);

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, txnType, 10m, result));
    }

    [Theory]
    [InlineData("P", "R")]
    [InlineData("R", "P")]
    [InlineData("P", null)]
    public void Transaction_type_mismatch_does_not_match(string expectedType, string? returnedType)
    {
        var result = Approved(returnedType, txnRef: TxnRef, amount: 10m);

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, expectedType, 10m, result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HB1234567890ABCDE")]
    [InlineData("HB\u00e91234")]
    [InlineData("HB\t1234")]
    [InlineData("ANZ:")]
    public void Invalid_expected_reference_never_matches(string? expectedTxnRef)
    {
        var result = Approved("P", txnRef: expectedTxnRef, amount: 10m);

        Assert.False(LinklyLocalTransactionIdentity.Matches(expectedTxnRef, "P", 10m, result));
    }

    [Theory]
    [InlineData("  HB1234567890ABCD  ", "HB1234567890ABCD")]
    [InlineData("ANZ:HB1234567890ABCD", "HB1234567890ABCD")]
    [InlineData("anz: HB1234567890ABCD ", "HB1234567890ABCD")]
    [InlineData("HB1234567890ABCD", "ANZ:HB1234567890ABCD")]
    public void Historical_anz_prefix_and_protocol_padding_are_normalized_on_both_sides(
        string expectedTxnRef,
        string returnedTxnRef)
    {
        var result = Approved("P", txnRef: returnedTxnRef, amount: 10m);

        Assert.True(LinklyLocalTransactionIdentity.Matches(expectedTxnRef, "P", 10m, result));
    }

    [Fact]
    public void Reference_comparison_is_case_sensitive()
    {
        var result = Approved("P", txnRef: TxnRef.ToLowerInvariant(), amount: 10m);

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Result_without_any_returned_reference_does_not_match()
    {
        // 终端没有回传任何 TxnRef 时无法证明是同一笔交易。
        var result = Approved("P", txnRef: null, reference: "   ", amount: 10m) with
        {
            CardTransactions = [CardTransaction(txnRef: null, amount: 10m)]
        };

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Reference_field_alone_can_prove_identity()
    {
        var result = Approved("P", txnRef: null, reference: "ANZ:" + TxnRef, amount: 10m);

        Assert.True(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Card_transaction_reference_alone_can_prove_identity()
    {
        var result = Approved("P", txnRef: null, amount: 10m) with
        {
            CardTransactions = [CardTransaction(txnRef: TxnRef, amount: 10m)]
        };

        Assert.True(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Blank_returned_reference_is_ignored_when_another_field_proves_identity()
    {
        var result = Approved("P", txnRef: "   ", reference: TxnRef, amount: 10m);

        Assert.True(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Conflicting_reference_field_rejects_even_when_txn_ref_matches()
    {
        var result = Approved("P", txnRef: TxnRef, reference: "HBOTHER000000000", amount: 10m);

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Conflicting_card_transaction_reference_rejects()
    {
        var result = Approved("P", txnRef: TxnRef, amount: 10m) with
        {
            CardTransactions =
            [
                CardTransaction(txnRef: TxnRef, amount: 10m),
                CardTransaction(txnRef: "HBOTHER000000000", amount: 10m)
            ]
        };

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Returned_reference_with_invalid_characters_rejects()
    {
        var result = Approved("P", txnRef: TxnRef, reference: TxnRef + "\u0001", amount: 10m);

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Card_transaction_amount_must_be_zero_or_expected_amount()
    {
        var zeroAmount = Approved("P", txnRef: TxnRef, amount: 10m) with
        {
            CardTransactions = [CardTransaction(txnRef: TxnRef, amount: 0m)]
        };
        var otherAmount = Approved("P", txnRef: TxnRef, amount: 10m) with
        {
            CardTransactions = [CardTransaction(txnRef: TxnRef, amount: 9.99m)]
        };

        Assert.True(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, zeroAmount));
        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, otherAmount));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("9.99")]
    [InlineData("10.01")]
    public void Approved_result_requires_exact_authorized_amount(string? authorizedAmount)
    {
        // 批准结果必须带回精确金额；缺失或为 0 都不能证明本笔已按原额扣款。
        var result = Approved("P", txnRef: TxnRef, amount: 10m) with
        {
            AuthorizedAmount = ParseAmount(authorizedAmount)
        };

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("10", true)]
    [InlineData("9.99", false)]
    [InlineData("20", false)]
    public void Declined_result_accepts_missing_zero_or_expected_amount(string? finalAmount, bool expected)
    {
        var result = new PaymentAuthorizationResult(
            false,
            TxnType: "P",
            TxnRef: TxnRef,
            AuthorizedAmount: ParseAmount(finalAmount));

        Assert.Equal(expected, LinklyLocalTransactionIdentity.Matches(TxnRef, "P", 10m, result));
    }

    [Fact]
    public void Declined_result_still_requires_returned_reference()
    {
        var result = new PaymentAuthorizationResult(false, TxnType: "R", AuthorizedAmount: 0m);

        Assert.False(LinklyLocalTransactionIdentity.Matches(TxnRef, "R", 10m, result));
    }

    private static PaymentAuthorizationResult Approved(
        string? txnType,
        string? txnRef,
        decimal amount,
        string? reference = null) =>
        new(
            true,
            Reference: reference,
            AuthorizedAmount: amount,
            TxnType: txnType,
            TxnRef: txnRef);

    private static decimal? ParseAmount(string? value) =>
        value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static CardTransactionDto CardTransaction(string? txnRef, decimal amount) =>
        new("Linkly", txnRef, "123456", "VISA", null, "411111******1111", null, "00", "APPROVED", null, null, amount, null);
}
