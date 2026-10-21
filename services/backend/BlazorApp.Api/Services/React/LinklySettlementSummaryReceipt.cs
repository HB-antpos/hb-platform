using System.Globalization;
using System.Text;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 按结算数据生成 24 列的「GRAND TOTALS」汇总小票文本。
/// 刷卡机发给 POS 的结算回单不带 Sales/Cash/Refund/Net 这一段（只在刷卡机自己打印的小票上），
/// 但结算数据里有完整金额，所以由后端在详情里补出来；调用方必须标明这是系统生成、不是终端原文。
/// </summary>
internal static class LinklySettlementSummaryReceipt
{
    private const int Width = 24;
    private const int LabelWidth = 8;
    private const int CountWidth = 4;
    private const int CardNameWidth = 11;
    private const int CardCountWidth = 3;

    public static string? Build(
        LinklySettlementAmountDto? summary,
        IReadOnlyList<LinklySettlementCardTotalDto> cardTotals)
    {
        if (summary is null)
            return null;

        var builder = new StringBuilder();
        builder.AppendLine("GRAND TOTALS");
        AppendLine(builder, "Sales", summary.PurchaseCount, summary.PurchaseAmountMinor);
        AppendLine(builder, "Cash", summary.CashOutCount, summary.CashOutAmountMinor);
        AppendLine(builder, "Refund", summary.RefundCount, summary.RefundAmountMinor);
        builder.AppendLine(new string('-', Width));
        var netAmount = FormatAmount(summary.TotalAmountMinor);
        builder.AppendLine("Net Amount".PadRight(Width - netAmount.Length) + netAmount);

        if (cardTotals.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("BY CARD (net)");
            foreach (var card in cardTotals)
            {
                var name = card.CardName.Length > CardNameWidth ? card.CardName[..CardNameWidth] : card.CardName;
                var amount = FormatAmount(card.TotalAmountMinor);
                var count = card.TotalCount.ToString(CultureInfo.InvariantCulture).PadLeft(CardCountWidth);
                builder.AppendLine(
                    name.PadRight(CardNameWidth)
                    + count
                    + amount.PadLeft(Width - CardNameWidth - CardCountWidth));
            }
        }

        return builder.ToString().TrimEnd();
    }

    // 标签 + 笔数 + 右对齐金额，整行 24 列，与刷卡机小票的宽度一致。
    private static void AppendLine(StringBuilder builder, string label, long count, long amountMinor)
    {
        var amount = FormatAmount(amountMinor);
        builder.AppendLine(
            label.PadRight(LabelWidth)
            + count.ToString(CultureInfo.InvariantCulture).PadLeft(CountWidth)
            + amount.PadLeft(Width - LabelWidth - CountWidth));
    }

    // 金额单位是分；负数写成 -$5.00，千分位用逗号。
    private static string FormatAmount(long amountMinor)
    {
        var sign = amountMinor < 0 ? "-" : string.Empty;
        var absolute = Math.Abs(amountMinor);
        var dollars = (absolute / 100).ToString("N0", CultureInfo.InvariantCulture);
        return $"{sign}${dollars}.{absolute % 100:D2}";
    }
}
