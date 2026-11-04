using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class PaymentSuccessCardSummaryTests
{
    [Fact]
    public void Cash_sale_with_change_puts_change_in_the_hero_and_amounts_below()
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            1234.56m,
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 1234.56m, null)],
            tendered: 1300m,
            change: 65.44m));

        Assert.Equal(PaymentSuccessHeroKind.Change, summary.HeroKind);
        Assert.True(summary.IsCashToCustomer);
        Assert.Equal("$65.44", summary.HeroAmountDisplay);
        Assert.Equal("success.card.change", summary.HeroTitleKey);
        Assert.Equal("$1234.56", summary.SummaryAmountDisplay);
        Assert.Equal("success.card.amountDue", summary.SummaryLabelKey);
        Assert.True(summary.IsCashReceivedVisible);
        Assert.Equal("$1300.00", summary.CashReceivedDisplay);
        Assert.False(summary.HasPaymentChips);
        Assert.Equal("PaymentSuccessful", summary.HeaderTitleKey);
        Assert.Equal("success.printReceipt", summary.PrintButtonTextKey);
    }

    [Fact]
    public void Exact_cash_shows_no_change_instead_of_a_big_zero()
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            50m,
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 50m, null)],
            tendered: 50m,
            change: 0m));

        Assert.Equal(PaymentSuccessHeroKind.NoChange, summary.HeroKind);
        Assert.False(summary.IsCashToCustomer);
        Assert.Equal("success.card.noChange", summary.HeroTitleKey);
        Assert.Equal("success.card.exactCash", summary.HeroSubtitleKey);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Card, "success.card.paidByCard")]
    [InlineData(PaymentMethodKind.Voucher, "success.card.paidByVoucher")]
    public void Non_cash_sale_shows_the_paid_amount(PaymentMethodKind method, string titleKey)
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            88m,
            [new ReceiptPaymentLine(method, 88m, null)]));

        Assert.Equal(PaymentSuccessHeroKind.Paid, summary.HeroKind);
        Assert.Equal(titleKey, summary.HeroTitleKey);
        Assert.Equal("$88.00", summary.HeroAmountDisplay);
        Assert.False(summary.IsCashReceivedVisible);
    }

    [Fact]
    public void Legacy_cash_order_without_change_does_not_guess_change()
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            20m,
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 20m, null)]));

        Assert.Equal(PaymentSuccessHeroKind.Paid, summary.HeroKind);
        Assert.Equal("success.card.paid", summary.HeroTitleKey);
    }

    [Fact]
    public void Mixed_payment_lists_each_method_and_notes_change_is_from_cash_only()
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            186m,
            [
                new ReceiptPaymentLine(PaymentMethodKind.Card, 100m, null),
                new ReceiptPaymentLine(PaymentMethodKind.Voucher, 20m, "VOUCHER:ABC"),
                new ReceiptPaymentLine(PaymentMethodKind.Cash, 66m, null)
            ],
            tendered: 80m,
            change: 14m));

        Assert.Equal(PaymentSuccessHeroKind.Change, summary.HeroKind);
        Assert.Equal("$14.00", summary.HeroAmountDisplay);
        Assert.Equal(
            [
                new PaymentSuccessPaymentChip("success.card.method.card", "$100.00"),
                new PaymentSuccessPaymentChip("success.card.method.voucher", "$20.00"),
                new PaymentSuccessPaymentChip("success.card.cashReceived", "$80.00")
            ],
            summary.PaymentChips);
        Assert.True(summary.IsChangeFromCashOnlyVisible);
        // 中文注释：有了明细标签就不再单独显示“实收现金”，避免同一个数出现两次。
        Assert.False(summary.IsCashReceivedVisible);
    }

    [Fact]
    public void Cash_refund_uses_the_same_cash_to_customer_hero_as_change()
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            -35.9m,
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, -35.9m, null)],
            lines:
            [
                new ReceiptPreviewLine("Tea", "930001", 2m, 10m, 0m, -20m),
                new ReceiptPreviewLine("Cup", "930002", 1m, 15.9m, 0m, -15.9m)
            ]));

        Assert.True(summary.IsRefund);
        Assert.Equal(PaymentSuccessHeroKind.CashRefund, summary.HeroKind);
        Assert.True(summary.IsCashToCustomer);
        Assert.Equal("$35.90", summary.HeroAmountDisplay);
        Assert.Equal("success.card.refundComplete", summary.HeaderTitleKey);
        Assert.Equal("success.card.refundTotal", summary.SummaryLabelKey);
        Assert.Equal("$35.90", summary.SummaryAmountDisplay);
        Assert.Equal("success.card.printRefundReceipt", summary.PrintButtonTextKey);
        Assert.True(summary.IsReturnedQuantityVisible);
        Assert.Equal("3", summary.ReturnedQuantityDisplay);
    }

    [Fact]
    public void Card_refund_tells_the_cashier_no_cash_goes_back()
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            -120m,
            [new ReceiptPaymentLine(PaymentMethodKind.Card, -120m, null)]));

        Assert.Equal(PaymentSuccessHeroKind.CardRefund, summary.HeroKind);
        Assert.True(summary.IsNonCashRefund);
        Assert.False(summary.IsCashToCustomer);
        Assert.Equal("success.card.cardRefund", summary.HeroTitleKey);
        Assert.Equal("success.card.noCashBack", summary.HeroSubtitleKey);
        Assert.Equal("$120.00", summary.HeroAmountDisplay);
    }

    [Fact]
    public void Voucher_refund_shows_the_voucher_amount_and_code()
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            -58m,
            [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -58m, "VOUCHER_REFUND:RV7D3E10F2A41B")],
            refundVoucher: new RefundVoucherReceipt("RV7D3E10F2A41B", 58m)));

        Assert.Equal(PaymentSuccessHeroKind.VoucherRefund, summary.HeroKind);
        Assert.Equal("success.card.voucherRefund", summary.HeroTitleKey);
        Assert.Equal("$58.00", summary.HeroAmountDisplay);
        Assert.True(summary.IsRefundVoucherCodeVisible);
        Assert.Equal("RV7D3E10F2A41B", summary.RefundVoucherCode);
    }

    [Fact]
    public void Mixed_refund_puts_cash_first_and_lists_every_refund_method()
    {
        var summary = PaymentSuccessCardSummary.Create(CreateReceipt(
            -80m,
            [
                new ReceiptPaymentLine(PaymentMethodKind.Card, -50m, null),
                new ReceiptPaymentLine(PaymentMethodKind.Cash, -30m, null)
            ]));

        Assert.Equal(PaymentSuccessHeroKind.CashRefund, summary.HeroKind);
        Assert.Equal("$30.00", summary.HeroAmountDisplay);
        Assert.Equal(
            [
                new PaymentSuccessPaymentChip("success.card.method.card", "$50.00"),
                new PaymentSuccessPaymentChip("success.card.method.cash", "$30.00")
            ],
            summary.PaymentChips);
        Assert.False(summary.IsChangeFromCashOnlyVisible);
    }

    [Fact]
    public void Loading_an_order_fills_the_card_summary()
    {
        var sale = new PaymentSuccessViewModel();
        Assert.Same(PaymentSuccessCardSummary.Empty, sale.CardSummary);

        sale.LoadFromOrder(CreateCashOrder());

        Assert.Equal(PaymentSuccessHeroKind.Change, sale.CardSummary.HeroKind);
        Assert.Equal("$5.00", sale.CardSummary.HeroAmountDisplay);
    }

    internal static ReceiptDetails CreateReceipt(
        decimal actualAmount,
        IReadOnlyList<ReceiptPaymentLine> payments,
        decimal? tendered = null,
        decimal? change = null,
        IReadOnlyList<ReceiptPreviewLine>? lines = null,
        RefundVoucherReceipt? refundVoucher = null) =>
        new(
            Guid.Parse("a1b2c3d4-e5f6-4711-8899-aabbccddeeff"),
            "1042",
            "POS-01",
            "Cashier",
            new DateTimeOffset(2026, 9, 26, 14, 45, 0, TimeSpan.FromHours(10)),
            actualAmount,
            0m,
            actualAmount,
            lines ?? [],
            payments,
            TenderedAmount: tendered,
            ChangeAmount: change,
            RefundVoucher: refundVoucher);

    private static LocalOrder CreateCashOrder()
    {
        return new LocalOrder(
            Guid.Parse("a1b2c3d4-e5f6-4711-8899-aabbccddeeff"),
            "1042",
            "POS-01",
            "cashier-1",
            "Cashier",
            new DateTimeOffset(2026, 9, 26, 14, 45, 0, TimeSpan.FromHours(10)),
            15m,
            0m,
            15m,
            [],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, 15m, null)],
            TenderedAmount: 20m,
            ChangeAmount: 5m);
    }
}
