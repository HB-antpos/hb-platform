using System.Globalization;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    /// <summary>
    /// 「更新进货价」涨跌幅保护：本单进货价较上次进货价涨跌绝对值超过 40% 时，必须带二次确认标志才允许写入商品主档与分店价。
    /// 典型场景是数量/价格按整箱录入，导致单价被放大或缩小数十倍。纯规则，不访问数据库。
    /// </summary>
    internal static class LocalSupplierInvoicesPurchasePriceChangeGuard
    {
        /// <summary>需要二次确认的涨跌幅阈值（绝对值，严格大于才触发，恰好 40% 放行）。</summary>
        public const decimal ConfirmRatio = 0.40m;

        /// <summary>未带二次确认标志被拒绝时返回给前端的错误码。</summary>
        public const string ConfirmRequiredCode = "PRICE_CHANGE_CONFIRM_REQUIRED";

        /// <summary>涨跌幅（本次 / 上次 - 1）；上次进货价为空或 ≤ 0（新商品、无可比价）、本次价为空时不可比较，返回 null。</summary>
        public static decimal? GetChangeRatio(decimal? lastPurchasePrice, decimal? purchasePrice)
        {
            if (!lastPurchasePrice.HasValue || lastPurchasePrice.Value <= 0m) return null;
            if (!purchasePrice.HasValue) return null;
            return purchasePrice.Value / lastPurchasePrice.Value - 1m;
        }

        public static bool IsLargeChange(decimal? lastPurchasePrice, decimal? purchasePrice) =>
            GetChangeRatio(lastPurchasePrice, purchasePrice) is { } ratio && Math.Abs(ratio) > ConfirmRatio;

        /// <summary>
        /// 找出本次将执行「更新进货价」且涨跌幅超限的明细。
        /// 已执行（99）的行不会再写价格，GetSavedAction 会把它归为 None，因此天然排除。
        /// 新进货价 ≤ 0 的行会被写入层跳过，不需要确认。
        /// </summary>
        public static List<StoreLocalSupplierInvoiceDetails> FindLargeChanges(
            IEnumerable<StoreLocalSupplierInvoiceDetails> details
        ) => details
            .Where(detail =>
                LocalSupplierInvoicesProductExecutionPlan.GetSavedAction(detail) == DetailAction.UpdatePurchasePrice
                && detail.PurchasePrice is > 0m
                && IsLargeChange(detail.LastPurchasePrice, detail.PurchasePrice))
            .ToList();

        public static string BuildErrorLine(StoreLocalSupplierInvoiceDetails detail)
        {
            var ratio = GetChangeRatio(detail.LastPurchasePrice, detail.PurchasePrice) ?? 0m;
            var percent = (ratio * 100m).ToString("+0.#;-0.#", CultureInfo.InvariantCulture);
            return $"明细 {detail.DetailGUID}（货号 {detail.ItemNumber ?? "--"} · {detail.ProductName ?? "--"}）"
                + $"进货价 {detail.LastPurchasePrice:0.####} → {detail.PurchasePrice:0.####}，较上次{percent}%，"
                + $"涨跌幅超过 {(ConfirmRatio * 100m).ToString("0", CultureInfo.InvariantCulture)}%，需二次确认";
        }
    }
}
