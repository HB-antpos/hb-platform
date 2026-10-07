using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 业务规则：代金券买的商品退货不能退现金（也不能退到卡上），代金券支付的那部分只能退代金券。
/// 按原单付款比例折算：每张原单本次退货金额 × 代金券付款占比（向上取整到分）必须以退款代金券退回；
/// 其余部分才允许现金/刷卡。按比例折算不依赖历史退货的分摊口径，累计下来退出的现金/卡款不会超过原非代金券付款。
/// </summary>
public static class VoucherFundedRefundPolicy
{
    /// <summary>
    /// 本次退款中必须以代金券退回的最低金额。无小票退货、缺原单标识的额度不参与（没有原付款可依据）。
    /// 同一张原单的额度被重复加入购物车时比例不变，结果不受影响。
    /// </summary>
    public static decimal GetRequiredVoucherRefundAmount(
        IReadOnlyList<OrderReturnPaymentCapacityDto> capacities,
        IEnumerable<CartLine> cartLines,
        decimal netRefundAmount)
    {
        var netRefund = Math.Abs(RoundCurrency(netRefundAmount));
        if (netRefund <= 0m || capacities.Count == 0)
        {
            return 0m;
        }

        var returnAmountByOrder = cartLines
            .Where(line => line.IsReturnLine && line.OriginalOrderGuid is not null)
            .GroupBy(line => line.OriginalOrderGuid!.Value)
            .ToDictionary(group => group.Key, group => Math.Abs(RoundCurrency(group.Sum(line => line.ActualAmount))));
        var required = 0m;
        foreach (var order in capacities
                     .Where(capacity => capacity.OriginalOrderGuid is not null && capacity.OriginalAmount > 0m)
                     .GroupBy(capacity => capacity.OriginalOrderGuid!.Value))
        {
            var paidTotal = order.Sum(capacity => capacity.OriginalAmount);
            var voucherPaid = order
                .Where(capacity => capacity.Method == PaymentMethodKind.Voucher)
                .Sum(capacity => capacity.OriginalAmount);
            if (voucherPaid <= 0m ||
                paidTotal <= 0m ||
                !returnAmountByOrder.TryGetValue(order.Key, out var returnAmount) ||
                returnAmount <= 0m)
            {
                continue;
            }

            required += CeilingToCent(returnAmount * Math.Min(1m, voucherPaid / paidTotal));
        }

        // 换货时新买商品先抵扣，实际退出的钱不足代金券部分时整笔退款都必须是代金券。
        return Math.Min(netRefund, RoundCurrency(required));
    }

    /// <summary>
    /// 现金/刷卡这一笔最多可退多少。voucherRefundRequired 为本次须退代金券的金额，
    /// existingTenders 为已加入的退款付款（负数），remainingRefundAmount 为当前尚未退出的金额（正数）。
    /// 代金券不受此上限约束，返回 null。
    /// </summary>
    public static decimal? GetNonVoucherRefundCap(
        PaymentMethodKind method,
        decimal netRefundAmount,
        decimal voucherRefundRequired,
        IReadOnlyList<PaymentTender> existingTenders,
        decimal remainingRefundAmount)
    {
        if (method == PaymentMethodKind.Voucher)
        {
            return null;
        }

        var netRefund = Math.Abs(RoundCurrency(netRefundAmount));
        var nonVoucherRefunded = Math.Abs(RoundCurrency(existingTenders
            .Where(tender => tender.Method != PaymentMethodKind.Voucher && tender.Amount < 0m)
            .Sum(tender => tender.Amount)));
        var allowance = Math.Max(0m, RoundCurrency(netRefund - voucherRefundRequired - nonVoucherRefunded));
        if (method != PaymentMethodKind.Cash)
        {
            return allowance;
        }

        // 现金必须最后退：代金券应退部分补足之前不允许现金。现有退款结算把"部分现金"后的剩余算成正数，
        // 现金不作为最后一笔会被误判为已结清；最后一笔按 5 分进位恰好结清剩余（最多差 2 分）。
        if (GetVoucherRefundShortfall(voucherRefundRequired, existingTenders) > 0m)
        {
            return 0m;
        }

        var remaining = Math.Abs(RoundCurrency(remainingRefundAmount));
        return remaining <= allowance
            ? new CashRoundingPolicy().NormalizeCashTender(remaining)
            : FloorToCashIncrement(allowance);
    }

    /// <summary>已加入的代金券退款是否已覆盖必须以代金券退回的金额。</summary>
    public static decimal GetVoucherRefundShortfall(
        decimal voucherRefundRequired,
        IReadOnlyList<PaymentTender> existingTenders)
    {
        var voucherRefunded = Math.Abs(RoundCurrency(existingTenders
            .Where(tender => tender.Method == PaymentMethodKind.Voucher && tender.Amount < 0m)
            .Sum(tender => tender.Amount)));
        return Math.Max(0m, RoundCurrency(voucherRefundRequired - voucherRefunded));
    }

    private static decimal CeilingToCent(decimal amount) =>
        Math.Ceiling(RoundCurrency(amount, 6) * 100m) / 100m;

    private static decimal FloorToCashIncrement(decimal amount) =>
        Math.Floor(RoundCurrency(amount) / CashRoundingPolicy.CashIncrement) * CashRoundingPolicy.CashIncrement;

    private static decimal RoundCurrency(decimal amount, int decimals = 2) =>
        decimal.Round(amount, decimals, MidpointRounding.AwayFromZero);
}
