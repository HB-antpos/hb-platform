using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// Zeller 引用格式：发给 Zeller 的 reference 由 attempt 派生且不可复用；订单引用同时带 reference 和 transactionUuid，
/// 退款靠 transactionUuid 唯一指向原交易。前缀识别错了会把退款路由到别的刷卡通道，所以逐条固定。
/// </summary>
public sealed class ZellerPaymentReferenceTests
{
    private static readonly Guid Attempt = Guid.Parse("799287a3-3dc2-4df1-bca3-d33eea19b35a");
    private const string TransactionUuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";

    [Fact]
    public void Purchase_reference_is_HBZ_plus_32_hex_and_36_characters()
    {
        var reference = ZellerPaymentReference.CreatePurchaseReference(Attempt);

        Assert.Equal("HBZ-799287a33dc24df1bca3d33eea19b35a", reference);
        Assert.Equal(36, reference.Length);
    }

    [Fact]
    public void Refund_reference_is_HBZR_plus_32_hex_and_37_characters()
    {
        var reference = ZellerPaymentReference.CreateRefundReference(Attempt);

        Assert.Equal("HBZR-799287a33dc24df1bca3d33eea19b35a", reference);
        Assert.Equal(37, reference.Length);
    }

    [Fact]
    public void Attempt_references_require_a_real_attempt()
    {
        Assert.Throws<ArgumentException>(() => ZellerPaymentReference.CreatePurchaseReference(Guid.Empty));
        Assert.Throws<ArgumentException>(() => ZellerPaymentReference.CreateRefundReference(Guid.Empty));
    }

    [Fact]
    public void Different_attempts_never_share_a_reference()
    {
        // Zeller 不对 reference 去重，复用会重复扣款。
        Assert.NotEqual(
            ZellerPaymentReference.CreatePurchaseReference(Guid.NewGuid()),
            ZellerPaymentReference.CreatePurchaseReference(Guid.NewGuid()));
    }

    [Fact]
    public void Payment_reference_round_trips_reference_and_transaction_uuid()
    {
        var purchase = ZellerPaymentReference.CreatePurchaseReference(Attempt);

        var formatted = ZellerPaymentReference.FormatPayment(purchase, TransactionUuid.ToUpperInvariant());

        Assert.Equal($"ZELLER:{purchase}:{TransactionUuid}", formatted);
        Assert.True(ZellerPaymentReference.TryParsePayment(formatted, out var parts));
        Assert.Equal(purchase, parts.Reference);
        Assert.Equal(TransactionUuid, parts.TransactionUuid);
    }

    [Fact]
    public void Refund_reference_round_trips_and_is_not_a_payment_reference()
    {
        var refund = ZellerPaymentReference.CreateRefundReference(Attempt);

        var formatted = ZellerPaymentReference.FormatRefund(refund, TransactionUuid);

        Assert.Equal($"ZELLERRF:{refund}:{TransactionUuid}", formatted);
        Assert.True(ZellerPaymentReference.TryParseRefund(formatted, out var parts));
        Assert.Equal(refund, parts.Reference);
        Assert.False(ZellerPaymentReference.TryParsePayment(formatted, out _));
    }

    [Theory]
    [InlineData("HBZR-799287a33dc24df1bca3d33eea19b35a")] // 退款 reference 不能当扣款 reference
    [InlineData("799287a33dc24df1bca3d33eea19b35a")]
    [InlineData("HBZ-not-a-guid")]
    [InlineData("")]
    public void Format_payment_rejects_references_that_are_not_purchase_attempts(string reference)
    {
        Assert.Throws<ArgumentException>(() => ZellerPaymentReference.FormatPayment(reference, TransactionUuid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Format_payment_rejects_missing_transaction_uuid(string transactionUuid)
    {
        var purchase = ZellerPaymentReference.CreatePurchaseReference(Attempt);

        Assert.Throws<ArgumentException>(() => ZellerPaymentReference.FormatPayment(purchase, transactionUuid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ZELLER:")]
    [InlineData("ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a")] // 缺 transactionUuid
    [InlineData("ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a:not-a-uuid")]
    [InlineData("ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a:65c4b343-03e6-49ef-a54d-012e3f2c222c:extra")]
    [InlineData("ZELLER:OTHER-799287a33dc24df1bca3d33eea19b35a:65c4b343-03e6-49ef-a54d-012e3f2c222c")]
    [InlineData("SQ:payment-1")]
    [InlineData("ANZ:TXN-1")]
    public void Try_parse_payment_rejects_malformed_or_foreign_references(string? reference)
    {
        Assert.False(ZellerPaymentReference.TryParsePayment(reference, out var parts));
        Assert.Equal(default(ZellerPaymentReferenceParts), parts);
    }

    [Theory]
    [InlineData("ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a:65c4b343-03e6-49ef-a54d-012e3f2c222c", true)]
    [InlineData("  zeller:broken  ", true)] // 损坏的 Zeller 引用仍然算 Zeller，不能回退到别的通道
    [InlineData("ZELLERRF:HBZR-799287a33dc24df1bca3d33eea19b35a:65c4b343-03e6-49ef-a54d-012e3f2c222c", true)]
    [InlineData("ZELLER", false)]
    [InlineData("SQ:payment-1", false)]
    [InlineData("SQRF:refund-1", false)]
    [InlineData("ANZ:TXN-1", false)]
    [InlineData("ANZCLOUD:TXN:RFN", false)]
    [InlineData("MANUAL:799287a33dc24df1bca3d33eea19b35a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Is_zeller_recognises_only_zeller_prefixes(string? reference, bool expected)
    {
        Assert.Equal(expected, ZellerPaymentReference.IsZeller(reference));
    }

    [Fact]
    public void Zeller_prefixes_do_not_collide_with_existing_prefix_sniffing()
    {
        // 现有代码用 StartsWith("SQ")、StartsWith("ANZ:") 判断通道，Zeller 的前缀不能被它们误判。
        foreach (var prefix in new[] { ZellerPaymentReference.PaymentPrefix, ZellerPaymentReference.RefundPrefix })
        {
            Assert.False(prefix.StartsWith("SQ", StringComparison.OrdinalIgnoreCase));
            Assert.False(prefix.StartsWith("ANZ", StringComparison.OrdinalIgnoreCase));
            Assert.False(ManualCardPaymentReference.IsManual(prefix));
        }
    }

    [Fact]
    public void Refund_source_is_recognised_through_the_card_refund_wrapper()
    {
        var payment = ZellerPaymentReference.FormatPayment(ZellerPaymentReference.CreatePurchaseReference(Attempt), TransactionUuid);
        var wrapped = CardRefundReference.Format("ZELLERRF:pending", payment);
        var squareWrapped = CardRefundReference.Format("SQRF:refund-1", "SQ:payment-1");

        Assert.True(ZellerPaymentReference.IsZellerRefundSource(payment));
        Assert.True(ZellerPaymentReference.IsZellerRefundSource(wrapped));
        Assert.False(ZellerPaymentReference.IsZellerRefundSource(squareWrapped));
        Assert.False(ZellerPaymentReference.IsZellerRefundSource(null));
    }

    [Fact]
    public void Original_transaction_uuid_comes_from_the_payment_reference_directly_or_wrapped()
    {
        var payment = ZellerPaymentReference.FormatPayment(ZellerPaymentReference.CreatePurchaseReference(Attempt), TransactionUuid);
        var wrapped = CardRefundReference.Format(null, payment);

        Assert.True(ZellerPaymentReference.TryGetOriginalTransactionUuid(payment, out var direct));
        Assert.Equal(TransactionUuid, direct);
        Assert.True(ZellerPaymentReference.TryGetOriginalTransactionUuid(wrapped, out var fromWrapper));
        Assert.Equal(TransactionUuid, fromWrapper);
    }

    [Theory]
    [InlineData("SQ:payment-1")]
    [InlineData("ZELLER:broken")]
    [InlineData("ZELLERRF:HBZR-799287a33dc24df1bca3d33eea19b35a:65c4b343-03e6-49ef-a54d-012e3f2c222c")] // 退款引用不是可退的原交易
    [InlineData(null)]
    public void Original_transaction_uuid_is_unavailable_for_anything_but_a_valid_zeller_payment(string? reference)
    {
        Assert.False(ZellerPaymentReference.TryGetOriginalTransactionUuid(reference, out var transactionUuid));
        Assert.Equal(string.Empty, transactionUuid);
    }
}
