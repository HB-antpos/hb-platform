using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖付款方式策略（现金 / 刷卡 / 代金券）：退款剩余额计算（现金按 0.05 取整、
/// 刷卡受原卡可退容量限制）、退款原卡引用、默认金额来源和加 tender 前的附加校验。
/// </summary>
public sealed class PaymentMethodStrategyTests
{
    // ── 退款剩余额 ──

    [Theory]
    [InlineData(-10.02, 10.00)]
    [InlineData(-10.03, 10.05)]
    [InlineData(-10.025, 10.05)]
    [InlineData(-0.02, 0.00)]
    [InlineData(7.47, 7.45)]
    [InlineData(0, 0)]
    public void Cash_refund_remaining_is_absolute_and_rounded_to_five_cents(decimal workflowRemaining, decimal expected)
    {
        var capacityCalls = 0;

        var remaining = CashStrategy.Instance.GetRefundRemainingAmount(
            workflowRemaining,
            _ =>
            {
                capacityCalls++;
                return 1m;
            });

        Assert.Equal(expected, remaining);
        // 现金退款不受原卡容量约束。
        Assert.Equal(0, capacityCalls);
    }

    [Fact]
    public void Card_refund_remaining_is_capped_by_next_original_card_capacity()
    {
        PaymentMethodKind? requestedMethod = null;

        var remaining = CardStrategy.Instance.GetRefundRemainingAmount(
            -25.555m,
            method =>
            {
                requestedMethod = method;
                return 12.30m;
            });

        Assert.Equal(12.30m, remaining);
        Assert.Equal(PaymentMethodKind.Card, requestedMethod);
    }

    [Fact]
    public void Card_refund_remaining_uses_rounded_net_amount_when_capacity_is_larger()
    {
        var remaining = CardStrategy.Instance.GetRefundRemainingAmount(-8.125m, _ => 100m);

        Assert.Equal(8.13m, remaining);
    }

    [Fact]
    public void Card_refund_remaining_is_zero_without_original_card_capacity()
    {
        // 没有可原路退回的刷卡记录时不能凭空给出刷卡退款额度。
        var remaining = CardStrategy.Instance.GetRefundRemainingAmount(-20m, _ => null);

        Assert.Equal(0m, remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.004)]
    [InlineData(-0.004)]
    public void Card_refund_remaining_short_circuits_when_nothing_left(decimal workflowRemaining)
    {
        var capacityCalls = 0;

        var remaining = CardStrategy.Instance.GetRefundRemainingAmount(
            workflowRemaining,
            _ =>
            {
                capacityCalls++;
                return 50m;
            });

        Assert.Equal(0m, remaining);
        Assert.Equal(0, capacityCalls);
    }

    [Theory]
    [InlineData(-15.555, 15.56)]
    [InlineData(15.554, 15.55)]
    [InlineData(0, 0)]
    [InlineData(-0.004, 0)]
    public void Voucher_refund_remaining_is_absolute_cent_rounded_and_unbounded_by_card_capacity(
        decimal workflowRemaining,
        decimal expected)
    {
        var capacityCalls = 0;

        var remaining = VoucherStrategy.Instance.GetRefundRemainingAmount(
            workflowRemaining,
            _ =>
            {
                capacityCalls++;
                return 1m;
            });

        Assert.Equal(expected, remaining);
        Assert.Equal(0, capacityCalls);
    }

    // ── 退款引用 ──

    [Fact]
    public void Only_card_strategy_resolves_original_card_reference()
    {
        var referenceCalls = 0;
        Func<string?> getReference = () =>
        {
            referenceCalls++;
            return "ANZ:TXN-ORIGINAL";
        };

        Assert.Null(CashStrategy.Instance.GetRefundReference(getReference));
        Assert.Null(VoucherStrategy.Instance.GetRefundReference(getReference));
        Assert.Equal(0, referenceCalls);

        Assert.Equal("ANZ:TXN-ORIGINAL", CardStrategy.Instance.GetRefundReference(getReference));
        Assert.Equal(1, referenceCalls);
    }

    [Fact]
    public void Card_strategy_passes_through_missing_reference()
    {
        Assert.Null(CardStrategy.Instance.GetRefundReference(() => null));
    }

    // ── 默认金额 ──

    [Fact]
    public void Cash_default_amount_uses_rounded_cash_remaining_for_sale_and_cash_refund_remaining_for_refund()
    {
        var sources = new AmountSources(cash: 9.95m, external: 9.97m, refund: 4.05m);

        Assert.Equal(9.95m, CashStrategy.Instance.ResolveDefaultAmount(false, 9.97m, sources.Cash, sources.External, sources.Refund));
        Assert.Equal(new[] { "cash" }, sources.TakeCalls());

        Assert.Equal(4.05m, CashStrategy.Instance.ResolveDefaultAmount(true, -9.97m, sources.Cash, sources.External, sources.Refund));
        Assert.Equal(new[] { "refund:Cash" }, sources.TakeCalls());
    }

    [Fact]
    public void Card_default_amount_uses_external_remaining_for_sale_and_card_refund_remaining_for_refund()
    {
        var sources = new AmountSources(cash: 9.95m, external: 9.97m, refund: 6m);

        Assert.Equal(9.97m, CardStrategy.Instance.ResolveDefaultAmount(false, 9.97m, sources.Cash, sources.External, sources.Refund));
        Assert.Equal(new[] { "external" }, sources.TakeCalls());

        Assert.Equal(6m, CardStrategy.Instance.ResolveDefaultAmount(true, -9.97m, sources.Cash, sources.External, sources.Refund));
        Assert.Equal(new[] { "refund:Card" }, sources.TakeCalls());
    }

    [Fact]
    public void Voucher_default_amount_uses_external_remaining_for_sale_and_voucher_refund_remaining_for_refund()
    {
        var sources = new AmountSources(cash: 9.95m, external: 9.97m, refund: 9.97m);

        Assert.Equal(9.97m, VoucherStrategy.Instance.ResolveDefaultAmount(false, 9.97m, sources.Cash, sources.External, sources.Refund));
        Assert.Equal(new[] { "external" }, sources.TakeCalls());

        Assert.Equal(9.97m, VoucherStrategy.Instance.ResolveDefaultAmount(true, -9.97m, sources.Cash, sources.External, sources.Refund));
        Assert.Equal(new[] { "refund:Voucher" }, sources.TakeCalls());
    }

    // ── CanAddTender 附加校验 ──

    [Theory]
    [InlineData(false, false, "", null)]
    [InlineData(true, false, "", null)]
    [InlineData(true, true, "", "")]
    public void Cash_never_blocks_tender(bool isRefundMode, bool allowDefaultAmount, string voucherCode, string? refundReference)
    {
        Assert.Null(CashStrategy.Instance.CanAddTenderAdditionalCheck(
            isRefundMode, allowDefaultAmount, voucherCode, refundReference, 1_000_000m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Card_refund_without_original_reference_is_blocked(string? refundReference)
    {
        var status = CardStrategy.Instance.CanAddTenderAdditionalCheck(
            isRefundMode: true,
            allowDefaultAmount: true,
            voucherCodeText: string.Empty,
            refundReference: refundReference,
            remainingAmount: 10m);

        Assert.Equal("payment.refund.status.noCardReference", status);
    }

    [Fact]
    public void Card_refund_with_reference_and_card_sale_are_allowed_without_upper_limit()
    {
        Assert.Null(CardStrategy.Instance.CanAddTenderAdditionalCheck(true, false, string.Empty, "ANZ:TXN-1", 0.01m));
        // 销售时没有原卡引用也允许，金额上限交由终端校验。
        Assert.Null(CardStrategy.Instance.CanAddTenderAdditionalCheck(false, false, string.Empty, null, 1_000_000m));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Voucher_sale_without_code_is_blocked_unless_default_amount_allowed(string voucherCode)
    {
        Assert.Equal(
            "payment.status.voucherCodeRequired",
            VoucherStrategy.Instance.CanAddTenderAdditionalCheck(false, false, voucherCode, null, 10m));
        Assert.Null(VoucherStrategy.Instance.CanAddTenderAdditionalCheck(false, true, voucherCode, null, 10m));
    }

    [Fact]
    public void Voucher_refund_and_voucher_sale_with_code_are_allowed()
    {
        // 退款生成新退款券，不需要收银员输入券码。
        Assert.Null(VoucherStrategy.Instance.CanAddTenderAdditionalCheck(true, false, string.Empty, null, 10m));
        Assert.Null(VoucherStrategy.Instance.CanAddTenderAdditionalCheck(false, false, "VC-001", null, 1_000_000m));
    }

    private sealed class AmountSources(decimal cash, decimal external, decimal refund)
    {
        private readonly List<string> _calls = [];

        public decimal Cash()
        {
            _calls.Add("cash");
            return cash;
        }

        public decimal External()
        {
            _calls.Add("external");
            return external;
        }

        public decimal Refund(PaymentMethodKind method)
        {
            _calls.Add("refund:" + method);
            return refund;
        }

        public string[] TakeCalls()
        {
            var calls = _calls.ToArray();
            _calls.Clear();
            return calls;
        }
    }
}
