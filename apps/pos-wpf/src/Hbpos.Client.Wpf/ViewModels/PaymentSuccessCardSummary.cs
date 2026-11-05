using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>主页支付成功卡片最醒目的那一块显示什么。</summary>
public enum PaymentSuccessHeroKind
{
    /// <summary>现金找零：要把钱交给顾客。</summary>
    Change,

    /// <summary>现金刚好付清：不要找钱。</summary>
    NoChange,

    /// <summary>非现金（或旧订单没有找零记录）：只显示已收款。</summary>
    Paid,

    /// <summary>退货退现金：要把钱交给顾客。</summary>
    CashRefund,

    /// <summary>退货退回银行卡：不用给现金。</summary>
    CardRefund,

    /// <summary>退货开了退款代金券：把打印的券交给顾客。</summary>
    VoucherRefund
}

public sealed record PaymentSuccessPaymentChip(string LabelKey, string AmountDisplay);

/// <summary>
/// 主页支付成功卡片的展示数据：只从已完成订单的小票明细推导，不改变小票与打印内容。
/// 要给顾客现金的情况（找零、退现金）用同一套醒目配色，其余金额降为次要信息。
/// </summary>
public sealed record PaymentSuccessCardSummary(
    PaymentSuccessHeroKind HeroKind,
    string HeroTitleKey,
    string HeroSubtitleKey,
    decimal HeroAmount,
    bool IsRefund,
    decimal SummaryAmount,
    decimal? CashReceived,
    IReadOnlyList<PaymentSuccessPaymentChip> PaymentChips,
    decimal ReturnedQuantity,
    string? RefundVoucherCode)
{
    public static PaymentSuccessCardSummary Empty { get; } = new(
        PaymentSuccessHeroKind.Paid,
        "success.card.paid",
        "success.card.noChange",
        0m,
        IsRefund: false,
        SummaryAmount: 0m,
        CashReceived: null,
        PaymentChips: [],
        ReturnedQuantity: 0m,
        RefundVoucherCode: null);

    public string HeaderTitleKey => IsRefund ? "success.card.refundComplete" : "PaymentSuccessful";

    public string SummaryLabelKey => IsRefund ? "success.card.refundTotal" : "success.card.amountDue";

    public string PrintButtonTextKey => IsRefund ? "success.card.printRefundReceipt" : "success.printReceipt";

    public string HeroAmountDisplay => FormatMoney(HeroAmount);

    public string SummaryAmountDisplay => FormatMoney(SummaryAmount);

    public string CashReceivedDisplay => CashReceived is { } amount ? FormatMoney(amount) : string.Empty;

    public string ReturnedQuantityDisplay => ReturnedQuantity.ToString("0.##", CultureInfo.InvariantCulture);

    public bool IsCashToCustomer => HeroKind is PaymentSuccessHeroKind.Change or PaymentSuccessHeroKind.CashRefund;

    public bool IsNoChange => HeroKind == PaymentSuccessHeroKind.NoChange;

    public bool IsPaid => HeroKind == PaymentSuccessHeroKind.Paid;

    public bool IsNonCashRefund => HeroKind is PaymentSuccessHeroKind.CardRefund or PaymentSuccessHeroKind.VoucherRefund;

    public bool IsCashReceivedVisible => CashReceived is not null && PaymentChips.Count == 0;

    public bool HasPaymentChips => PaymentChips.Count > 0;

    // 中文注释：混合支付有找零时说明找零只按现金部分算，避免收银员拿应收去减实收。
    public bool IsChangeFromCashOnlyVisible => HasPaymentChips && HeroKind == PaymentSuccessHeroKind.Change;

    public bool IsReturnedQuantityVisible => IsRefund && ReturnedQuantity > 0m;

    public bool IsRefundVoucherCodeVisible => !string.IsNullOrWhiteSpace(RefundVoucherCode);

    public static PaymentSuccessCardSummary Create(ReceiptDetails receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.ActualAmount < 0m ? CreateRefund(receipt) : CreateSale(receipt);
    }

    private static PaymentSuccessCardSummary CreateSale(ReceiptDetails receipt)
    {
        var payments = receipt.Payments.Where(payment => payment.Amount > 0m).ToArray();
        var hasCash = payments.Any(payment => payment.Method == PaymentMethodKind.Cash);
        var methods = payments.Select(payment => payment.Method).Distinct().ToArray();
        var chips = methods.Length > 1 ? CreateSaleChips(payments, receipt.TenderedAmount) : [];

        // 中文注释：旧订单没有持久化找零字段时不推测找零，与成功页原有规则一致。
        var (kind, title, subtitle, amount) = hasCash && receipt.ChangeAmount is { } change
            ? change > 0m
                ? (PaymentSuccessHeroKind.Change, "success.card.change", "success.card.giveToCustomer", change)
                : (PaymentSuccessHeroKind.NoChange, "success.card.noChange", "success.card.exactCash", 0m)
            : (PaymentSuccessHeroKind.Paid, ResolvePaidTitleKey(methods), "success.card.noChange", receipt.ActualAmount);

        return new PaymentSuccessCardSummary(
            kind,
            title,
            subtitle,
            amount,
            IsRefund: false,
            SummaryAmount: receipt.ActualAmount,
            CashReceived: hasCash ? receipt.TenderedAmount : null,
            PaymentChips: chips,
            ReturnedQuantity: 0m,
            RefundVoucherCode: null);
    }

    private static PaymentSuccessCardSummary CreateRefund(ReceiptDetails receipt)
    {
        var refunds = receipt.Payments.Where(payment => payment.Amount < 0m).ToArray();
        var cashRefund = SumRefund(refunds, PaymentMethodKind.Cash);
        var cardRefund = SumRefund(refunds, PaymentMethodKind.Card);
        var voucherRefund = SumRefund(refunds, PaymentMethodKind.Voucher);
        var refundTotal = decimal.Abs(receipt.ActualAmount);
        var methods = refunds.Select(payment => payment.Method).Distinct().ToArray();
        var chips = methods.Length > 1
            ? methods.Select(method => new PaymentSuccessPaymentChip(
                    MethodLabelKey(method),
                    FormatMoney(SumRefund(refunds, method))))
                .ToArray()
            : [];

        // 中文注释：只要有现金要退给顾客就把现金放在最醒目处；其次是退款代金券，最后才是退卡。
        var (kind, title, subtitle, amount) = cashRefund > 0m
            ? (PaymentSuccessHeroKind.CashRefund, "success.card.cashRefund", "success.card.giveCashToCustomer", cashRefund)
            : voucherRefund > 0m
                ? (PaymentSuccessHeroKind.VoucherRefund, "success.card.voucherRefund", "success.card.giveVoucherToCustomer", voucherRefund)
                : (PaymentSuccessHeroKind.CardRefund,
                    cardRefund > 0m ? "success.card.cardRefund" : "success.card.refunded",
                    "success.card.noCashBack",
                    cardRefund > 0m ? cardRefund : refundTotal);

        return new PaymentSuccessCardSummary(
            kind,
            title,
            subtitle,
            amount,
            IsRefund: true,
            SummaryAmount: refundTotal,
            CashReceived: null,
            PaymentChips: chips,
            ReturnedQuantity: receipt.Lines
                .Where(line => line.ActualAmount < 0m)
                .Sum(line => decimal.Abs(line.Quantity)),
            RefundVoucherCode: receipt.RefundVoucher?.VoucherCode);
    }

    private static PaymentSuccessPaymentChip[] CreateSaleChips(
        IReadOnlyList<ReceiptPaymentLine> payments,
        decimal? tenderedAmount)
    {
        return payments
            .Select(payment => payment.Method)
            .Distinct()
            .Select(method => method == PaymentMethodKind.Cash && tenderedAmount is { } tendered
                ? new PaymentSuccessPaymentChip("success.card.cashReceived", FormatMoney(tendered))
                : new PaymentSuccessPaymentChip(
                    MethodLabelKey(method),
                    FormatMoney(payments.Where(payment => payment.Method == method).Sum(payment => payment.Amount))))
            .ToArray();
    }

    private static string ResolvePaidTitleKey(IReadOnlyList<PaymentMethodKind> methods)
    {
        return methods.Count == 1
            ? methods[0] switch
            {
                PaymentMethodKind.Card => "success.card.paidByCard",
                PaymentMethodKind.Voucher => "success.card.paidByVoucher",
                _ => "success.card.paid"
            }
            : "success.card.paid";
    }

    private static decimal SumRefund(IEnumerable<ReceiptPaymentLine> refunds, PaymentMethodKind method)
    {
        return decimal.Abs(refunds.Where(payment => payment.Method == method).Sum(payment => payment.Amount));
    }

    private static string MethodLabelKey(PaymentMethodKind method)
    {
        return method switch
        {
            PaymentMethodKind.Cash => "success.card.method.cash",
            PaymentMethodKind.Card => "success.card.method.card",
            _ => "success.card.method.voucher"
        };
    }

    // 中文注释：与成功页其余金额同一格式（$1234.56，不加千分位）。
    private static string FormatMoney(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }
}
