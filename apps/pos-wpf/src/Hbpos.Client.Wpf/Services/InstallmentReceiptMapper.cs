using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

internal static class InstallmentReceiptMapper
{
    public static ReceiptDetails CreateReceipt(LocalInstallmentOrder order)
    {
        var recordedPayments = order.Payments
            .Where(payment => payment.Status == InstallmentPaymentStatus.Recorded)
            .OrderBy(payment => payment.RecordedAt)
            .ToList();
        var payments = recordedPayments
            .Select(payment => new ReceiptPaymentLine(
                payment.Method,
                payment.Amount,
                payment.Reference,
                payment.CardTransactions))
            .ToList();
        var extraInfoLines = new List<string>
        {
            $"Installment No: {order.InstallmentNumber}",
            $"Customer: {order.CustomerName}",
            $"Phone: {order.CustomerPhone}",
            $"Deposit paid: {Money(order.DownPaymentAmount)}",
            $"Balance due: {Money(order.BalanceAmount)}"
        };

        AddPickupInfo(extraInfoLines, order);

        if (recordedPayments.Count > 0)
        {
            // 分期补录打印要展示完整还款历史；时间只存在于分期 payment DTO，不能放到通用 ReceiptPaymentLine。
            extraInfoLines.Add("Payment history:");
            extraInfoLines.AddRange(recordedPayments.Select(FormatPaymentHistoryLine));
        }

        return new ReceiptDetails(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.CreatedAt,
            order.TotalAmount,
            0m,
            order.TotalAmount,
            order.Lines.Select(line => new ReceiptPreviewLine(
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount)
            {
                ProductCode = line.ProductCode,
                ItemNumber = line.ItemNumber
            }).ToList(),
            payments,
            StatusText: GetStatusText(order),
            OrderDisplay: order.InstallmentNumber,
            ExtraInfoLines: extraInfoLines,
            Terms: GetTerms(order));
    }

    /// <summary>
    /// 分期条款（英文，业主审定稿）。金额门槛与服务端 InstallmentService 的
    /// MinimumInstallmentTotalAmount(50) / MinimumDownPaymentAmount(20) 保持一致；
    /// 「每次 $5」只是说明文字，系统不做强制校验（业主 2026-10-07 决定），最后一笔余额不足 $5 时按余额收。
    /// 这里只是未定制时的默认正文；总部按门店定制后由 <see cref="ReceiptTextFormatter"/> 渲染时套用（见 <see cref="ReceiptTermsText"/>）。
    /// </summary>
    internal static readonly ReceiptTerms InstallmentTerms = new(
        "INSTALLMENT TERMS",
        [
            "Order total: $50.00 minimum.",
            "First payment: $20.00 minimum.",
            "Each later payment: $5.00 minimum, or the remaining balance if it is lower."
        ],
        ReceiptTermsKind.Installment);

    private static ReceiptTerms? GetTerms(LocalInstallmentOrder order)
    {
        // 中文注释：条款只对仍在还款的分期单有意义；已付清、已提货、已取消的小票不再打印，避免取消单被误导。
        return order.Status == InstallmentStatus.Active && order.PickupInfo is null
            ? InstallmentTerms
            : null;
    }

    /// <summary>
    /// 取消分期时签发的退款代金券（已记录的负数代金券付款、引用为 VOUCHER_REFUND:券码），按记录时间排序。
    /// </summary>
    public static IReadOnlyList<RefundVoucherReceipt> GetRefundVouchers(LocalInstallmentOrder order)
    {
        const string prefix = "VOUCHER_REFUND:";
        return order.Payments
            .Where(payment =>
                payment.Status == InstallmentPaymentStatus.Recorded &&
                payment.Method == PaymentMethodKind.Voucher &&
                payment.Amount < 0m)
            .OrderBy(payment => payment.RecordedAt)
            .Select(payment => (Reference: payment.Reference?.Trim(), Amount: Math.Abs(payment.Amount)))
            .Where(item => item.Reference?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => new RefundVoucherReceipt(item.Reference![prefix.Length..].Trim(), item.Amount))
            .Where(voucher => voucher.VoucherCode.Length > 0)
            .ToList();
    }

    /// <summary>
    /// 单张退款券凭证：只保留这张券的付款行，避免打印成功后把分期单里的刷卡回单误标为已打印。
    /// </summary>
    public static ReceiptDetails CreateRefundVoucherReceipt(LocalInstallmentOrder order, RefundVoucherReceipt voucher) =>
        CreateReceipt(order) with
        {
            Payments = [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -voucher.Amount, $"VOUCHER_REFUND:{voucher.VoucherCode}")],
            RefundVoucher = voucher,
            VoucherBalance = null
        };

    private static void AddPickupInfo(List<string> extraInfoLines, LocalInstallmentOrder order)
    {
        if (order.PickupInfo is { } pickupInfo)
        {
            extraInfoLines.Add("Pickup: Confirmed");
            extraInfoLines.Add($"Picked up at: {pickupInfo.PickedUpAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            extraInfoLines.Add($"Picked up by: {pickupInfo.PickedUpBy}");
            if (!string.IsNullOrWhiteSpace(pickupInfo.Note))
            {
                extraInfoLines.Add($"Pickup note: {pickupInfo.Note}");
            }

            return;
        }

        if (order.Status == InstallmentStatus.PaidOff)
        {
            extraInfoLines.Add("Pickup: Pending");
        }
    }

    private static string GetStatusText(LocalInstallmentOrder order)
    {
        // 分期付清后小票必须明确提货状态，避免把待提货误看成已交付。
        if (order.PickupInfo is not null || order.Status == InstallmentStatus.PickedUp)
        {
            return "*** Paid - Picked Up ***";
        }

        if (order.Status == InstallmentStatus.Cancelled)
        {
            return "*** Installment Cancelled ***";
        }

        if (order.Status == InstallmentStatus.PaidOff)
        {
            return "*** Paid - Pickup Pending ***";
        }

        return "*** Deposit Received ***";
    }

    private static string FormatPaymentHistoryLine(InstallmentPaymentDto payment)
    {
        var timeText = payment.RecordedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var paymentLine = new ReceiptPaymentLine(payment.Method, payment.Amount, payment.Reference, payment.CardTransactions);
        var line = $"{timeText} {paymentLine.MethodLabel} {Money(payment.Amount)}";
        return string.IsNullOrWhiteSpace(paymentLine.DisplayReference)
            ? line
            : $"{line} Ref: {paymentLine.DisplayReference}";
    }

    private static string Money(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }
}
