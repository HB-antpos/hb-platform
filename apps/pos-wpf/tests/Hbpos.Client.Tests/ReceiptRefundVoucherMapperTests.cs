using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖退款代金券小票映射：只有「唯一一笔、负额、代金券、VOUCHER_REFUND: 前缀且带券码」
/// 的付款行才能打印成独立退款券；本地与远程小票详情共用该规则。
/// </summary>
public sealed class ReceiptRefundVoucherMapperTests
{
    [Fact]
    public void Single_negative_voucher_refund_line_maps_to_refund_voucher_with_positive_amount()
    {
        var voucher = ReceiptRefundVoucherMapper.TryCreate(
        [
            new ReceiptPaymentLine(PaymentMethodKind.Voucher, -12.35m, "VOUCHER_REFUND:RF-0001")
        ]);

        Assert.NotNull(voucher);
        Assert.Equal("RF-0001", voucher.VoucherCode);
        Assert.Equal(12.35m, voucher.Amount);
    }

    [Theory]
    [InlineData("  VOUCHER_REFUND:  RF-0002  ", "RF-0002")]
    [InlineData("voucher_refund:rf-lower", "rf-lower")]
    [InlineData("Voucher_Refund:RF:WITH:COLONS", "RF:WITH:COLONS")]
    public void Prefix_is_case_insensitive_and_code_is_trimmed_but_otherwise_preserved(string reference, string expectedCode)
    {
        var voucher = ReceiptRefundVoucherMapper.TryCreate(
        [
            new ReceiptPaymentLine(PaymentMethodKind.Voucher, -5m, reference)
        ]);

        Assert.NotNull(voucher);
        Assert.Equal(expectedCode, voucher.VoucherCode);
        Assert.Equal(5m, voucher.Amount);
    }

    [Fact]
    public void Empty_payment_list_does_not_map()
    {
        Assert.Null(ReceiptRefundVoucherMapper.TryCreate([]));
    }

    [Fact]
    public void Split_refund_with_voucher_and_cash_lines_does_not_map()
    {
        // 多笔退款（券 + 现金）不是独立退款券小票，否则会把整单印成只有券面额。
        var voucher = ReceiptRefundVoucherMapper.TryCreate(
        [
            new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, "VOUCHER_REFUND:RF-SPLIT"),
            new ReceiptPaymentLine(PaymentMethodKind.Cash, -2m, null)
        ]);

        Assert.Null(voucher);
    }

    [Fact]
    public void Two_voucher_refund_lines_do_not_map()
    {
        var voucher = ReceiptRefundVoucherMapper.TryCreate(
        [
            new ReceiptPaymentLine(PaymentMethodKind.Voucher, -3m, "VOUCHER_REFUND:RF-A"),
            new ReceiptPaymentLine(PaymentMethodKind.Voucher, -4m, "VOUCHER_REFUND:RF-B")
        ]);

        Assert.Null(voucher);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Non_negative_voucher_amount_does_not_map(decimal amount)
    {
        // 正额是用券消费，零额没有退款意义；二者都不能印成退款券。
        var voucher = ReceiptRefundVoucherMapper.TryCreate(
        [
            new ReceiptPaymentLine(PaymentMethodKind.Voucher, amount, "VOUCHER_REFUND:RF-0003")
        ]);

        Assert.Null(voucher);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash)]
    [InlineData(PaymentMethodKind.Card)]
    public void Refund_reference_on_non_voucher_method_does_not_map(PaymentMethodKind method)
    {
        var voucher = ReceiptRefundVoucherMapper.TryCreate(
        [
            new ReceiptPaymentLine(method, -8m, "VOUCHER_REFUND:RF-0004")
        ]);

        Assert.Null(voucher);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("VOUCHER_REFUND_PENDING")]
    [InlineData("VOUCHER_REFUND")]
    [InlineData("VOUCHER:RF-0005")]
    [InlineData("REFUND:VOUCHER_REFUND:RF-0005")]
    [InlineData("VOUCHER_REFUND:")]
    [InlineData("VOUCHER_REFUND:    ")]
    public void Missing_or_malformed_refund_reference_does_not_map(string? reference)
    {
        var voucher = ReceiptRefundVoucherMapper.TryCreate(
        [
            new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, reference)
        ]);

        Assert.Null(voucher);
    }

    [Fact]
    public void Lazy_payment_sequence_is_enumerated_only_once()
    {
        var enumerations = 0;
        IEnumerable<ReceiptPaymentLine> Payments()
        {
            enumerations++;
            yield return new ReceiptPaymentLine(PaymentMethodKind.Voucher, -6.5m, "VOUCHER_REFUND:RF-LAZY");
        }

        var voucher = ReceiptRefundVoucherMapper.TryCreate(Payments());

        Assert.NotNull(voucher);
        Assert.Equal("RF-LAZY", voucher.VoucherCode);
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void Card_transactions_on_voucher_line_do_not_affect_mapping()
    {
        var voucher = ReceiptRefundVoucherMapper.TryCreate(
        [
            new ReceiptPaymentLine(
                PaymentMethodKind.Voucher,
                -9.99m,
                "VOUCHER_REFUND:RF-0006",
                [new CardTransactionDto("Linkly", "TXN", null, null, null, null, null, null, null, null, null, 9.99m, null)])
        ]);

        Assert.NotNull(voucher);
        Assert.Equal(9.99m, voucher.Amount);
    }
}
