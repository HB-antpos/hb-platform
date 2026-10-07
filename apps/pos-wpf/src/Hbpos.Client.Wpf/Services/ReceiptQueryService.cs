using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface IReceiptQueryService
{
    Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
        LocalOrderHistoryQuery query,
        int take = 50,
        CancellationToken cancellationToken = default);

    Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default);
}

public sealed class ReceiptQueryService(ILocalOrderRepository orderRepository) : IReceiptQueryService
{
    public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => orderRepository.GetRecentOrdersAsync(take, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
        LocalOrderHistoryQuery query,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var querySnapshot = query with { };
        return Task.Run(
            () => orderRepository.GetRecentOrdersAsync(querySnapshot, take, cancellationToken),
            cancellationToken);
    }

    public async Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            var order = await orderRepository.GetOrderAsync(orderGuid, cancellationToken);
            return order is null ? null : CreateReceipt(order);
        }, cancellationToken);
    }

    public async Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            var latest = (await orderRepository.GetRecentOrdersAsync(1, cancellationToken)).FirstOrDefault();
            if (latest is null)
            {
                return null;
            }

            var order = await orderRepository.GetOrderAsync(latest.OrderGuid, cancellationToken);
            return order is null ? null : CreateReceipt(order);
        }, cancellationToken);
    }

    public static ReceiptDetails CreateReceipt(LocalOrder order)
    {
        // 小票详情补带本地持久化的实收/找零，避免界面只能依赖瞬时导航参数。
        var payments = order.Payments.Select(payment => new ReceiptPaymentLine(
            payment.Method,
            payment.Amount,
            payment.Reference,
            payment.CardTransactions)).ToList();
        return new ReceiptDetails(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SoldAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
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
            order.TenderedAmount,
            order.ChangeAmount,
            RefundVoucher: ReceiptRefundVoucherMapper.TryCreate(payments));
    }
}

internal static class ReceiptRefundVoucherMapper
{
    // 本地与远程详情统一按小票付款行判断，避免两条收据路径的退款券语义漂移。
    public static RefundVoucherReceipt? TryCreate(IEnumerable<ReceiptPaymentLine> payments)
    {
        var paymentList = payments.ToList();
        if (paymentList.Count != 1)
        {
            return null;
        }

        var refundPayment = paymentList[0];
        if (refundPayment.Method != PaymentMethodKind.Voucher || refundPayment.Amount >= 0m)
        {
            return null;
        }

        const string prefix = "VOUCHER_REFUND:";
        var reference = refundPayment.Reference?.Trim();
        if (string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var voucherCode = reference[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(voucherCode)
            ? null
            : new RefundVoucherReceipt(voucherCode, decimal.Abs(refundPayment.Amount));
    }
}

/// <summary>
/// 退款代金券券面数据。ExpiresAt 是服务端那张券自己的真实到期时刻（旧券 12 个月、新券 90 天规则不同，
/// 客户端不能按规则反推），由打印服务在出票前按券号补查；查不到时为 null，券面不印日期但照常出票。
/// </summary>
public sealed record RefundVoucherReceipt(string VoucherCode, decimal Amount, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// 条款块的种类：决定打印时套用本机设置里的哪一段定制正文（总部下发，见 <see cref="ReceiptTermsText"/>）。
/// </summary>
public enum ReceiptTermsKind
{
    /// <summary>固定条款，正文不可定制。</summary>
    Fixed,

    /// <summary>分期条款（INSTALLMENT TERMS），正文可由总部按门店定制。</summary>
    Installment,

    /// <summary>退款代金券使用说明（VOUCHER TERMS），正文可由总部按门店定制。</summary>
    RefundVoucher
}

/// <summary>
/// 小票底部的条款块（标题 + 若干条款行）；只有显式带上的小票才会打印，其余小票输出保持不变。
/// <see cref="Lines"/> 是内置默认正文，<see cref="Kind"/> 标明该块是否允许被本机设置里的定制正文替换。
/// </summary>
public sealed record ReceiptTerms(string Title, IReadOnlyList<string> Lines, ReceiptTermsKind Kind = ReceiptTermsKind.Fixed);

/// <summary>
/// 券面使用说明（英文，业主审定稿）。只对退款代金券：这类券由服务端绑定发券门店（别的门店会被拒绝）、
/// 支持分次使用（余额保留）；「不可兑现」与业主「代金券买的商品不能退现金」的规则一致。
/// 余额凭证对应的券类型不限于退款券，不一定限本店，所以不套用这段文字。
/// </summary>
internal static class VoucherReceiptTerms
{
    public static readonly ReceiptTerms RefundVoucher = new(
        "VOUCHER TERMS",
        [
            "Use at the issuing store only.",
            "Pay with it at checkout by scanning the barcode or QR code.",
            "Can be used across several purchases until the balance is $0.00.",
            "Not redeemable for cash."
        ],
        ReceiptTermsKind.RefundVoucher);
}

internal static class ReceiptRefundVoucherDocuments
{
    private const string Prefix = "VOUCHER_REFUND:";

    /// <summary>
    /// 混合退款（刷卡/现金 + 退款代金券）中已签发的每张退款券各生成一张独立凭证；
    /// 凭证只保留该券付款行，避免打印成功后把同单刷卡回单误标为已打印。待签发（无券码）的不出票。
    /// </summary>
    public static IReadOnlyList<ReceiptDetails> Create(ReceiptDetails receipt) =>
        receipt.Payments
            .Where(payment => payment.Method == PaymentMethodKind.Voucher && payment.Amount < 0m)
            .Select(payment => (Payment: payment, Reference: payment.Reference?.Trim()))
            .Where(item => item.Reference?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true &&
                item.Reference.Length > Prefix.Length)
            .Select(item => receipt with
            {
                Payments = [item.Payment],
                RefundVoucher = new RefundVoucherReceipt(item.Reference![Prefix.Length..].Trim(), decimal.Abs(item.Payment.Amount)),
                VoucherBalance = null
            })
            .Where(document => document.RefundVoucher!.VoucherCode.Length > 0)
            .ToList();
}

/// <summary>余额凭证券面数据；ExpiresAt 含义同退款券（出票前补查，查不到为 null）。</summary>
public sealed record VoucherBalanceReceipt(string VoucherCode, decimal RemainingBalance, DateTimeOffset? ExpiresAt = null);

public sealed record ReceiptDetails(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<ReceiptPreviewLine> Lines,
    IReadOnlyList<ReceiptPaymentLine> Payments,
    decimal? TenderedAmount = null,
    decimal? ChangeAmount = null,
    string? DocumentTitle = null,
    string? StatusText = null,
    string? OrderDisplay = null,
    IReadOnlyList<string>? ExtraInfoLines = null,
    RefundVoucherReceipt? RefundVoucher = null,
    VoucherBalanceReceipt? VoucherBalance = null,
    ReceiptTerms? Terms = null)
{
    public string TransactionIdDisplay => $"#{OrderGuid.ToString("N")[..10].ToUpperInvariant()}";

    public string SoldAtDisplay => SoldAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");
}
