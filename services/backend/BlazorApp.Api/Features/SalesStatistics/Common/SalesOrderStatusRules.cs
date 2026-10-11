namespace BlazorApp.Api.Services;

/// <summary>
/// POSM sales_order.Status 在销售统计里的统一口径。
///
/// 旧 POS 的退款是一张独立的退款单（Status=3）：支付明细、销售明细都是负数。
/// HBSales 旧系统历史本来就把退货单取反计入（净额），日结小票的 Total Income 也是扣掉退款的净额，
/// 所以营业额、销量这类“金额/数量”指标必须把 Status=3 一并汇总，才能与小票、历史口径对上。
/// 订单数、客数、客单价分母数的是真实成交单，退款单不算一单。
/// 新 POS 的退货是 Status=1 订单里的负数支付 + sales_return_record，本来就按净额进入统计，不受此影响。
/// </summary>
internal static class SalesOrderStatusRules
{
    internal const int Paid = 1;
    internal const int Refunded = 3;
    internal const int Installment = 4;

    /// <summary>金额、数量（含负数退款单）的订单状态集合：已支付、退款单、分期付。</summary>
    internal static readonly int?[] NetSalesStatuses = [Paid, Refunded, Installment];

    /// <summary>订单数、客数的订单状态集合：只数真实成交单，不含退款单。</summary>
    internal static readonly int?[] OrderCountStatuses = [Paid, Installment];

    internal static bool IsRefundOrder(int? status) => status == Refunded;

    /// <summary>该订单是否应计入订单数/客数。</summary>
    internal static bool CountsAsOrder(int? status) => status is Paid or Installment;
}
