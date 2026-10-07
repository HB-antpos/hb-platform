using System.Text.RegularExpressions;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 小票底部条款块的正文取值规则：退款代金券的「VOUCHER TERMS」与进行中分期小票的「INSTALLMENT TERMS」，
/// 标题由收银端固定打印，标题下面的正文可由总部按门店定制（与手持 / iPad / Web 同一套语义）。
/// <list type="bullet">
/// <item>null / 空串 / 纯空白＝未定制，按内置默认文案打印（默认文案逐字不变）。</item>
/// <item>定制正文是多行纯文本，一行一条：按 CR LF / CR / LF 拆行 → 逐行 trim → 丢弃空行，其余原样作为正文行。</item>
/// <item>拆完一行都没有（例如全是空行）也退回默认文案，不会印出只有标题的空条款块。</item>
/// </list>
/// 所有渲染路径（现场打印、重打印、远程历史补打、预览）都经 <see cref="ReceiptTextFormatter"/>，
/// 取值统一走这里，避免多处各抄一份。
/// </summary>
public static class ReceiptTermsText
{
    /// <summary>定制正文的长度上限（UTF-16 码元），与服务端、手持 / iPad 一致；超限的下发快照整份丢弃。</summary>
    public const int MaxLength = 600;

    private static readonly Regex LineBreak = new(@"\r\n|\r|\n", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>归一：trim 首尾空白，纯空白（含 null）归为 null。本机存储 / 界面回显 / 比较统一用这个口径。</summary>
    public static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>按行拆分定制正文：逐行 trim 并丢弃空行；没有可打印行时返回空列表。</summary>
    public static IReadOnlyList<string> SplitLines(string? customText)
    {
        if (string.IsNullOrWhiteSpace(customText))
        {
            return [];
        }

        return LineBreak.Split(customText)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <summary>
    /// 取最终要打印的正文行：定制正文拆出至少一行就用定制的，否则退回 <paramref name="defaultLines"/>（原样返回同一个实例）。
    /// </summary>
    public static IReadOnlyList<string> ResolveLines(string? customText, IReadOnlyList<string> defaultLines)
    {
        var lines = SplitLines(customText);
        return lines.Count > 0 ? lines : defaultLines;
    }

    /// <summary>
    /// 把本机设置里的定制正文套到条款块上：<see cref="ReceiptTermsKind.Installment"/> 用分期条款、
    /// <see cref="ReceiptTermsKind.RefundVoucher"/> 用代金券使用说明；<see cref="ReceiptTermsKind.Fixed"/> 原样返回。
    /// 标题永远保持固定，只替换正文行。
    /// </summary>
    public static ReceiptTerms ApplyCustomBody(ReceiptTerms terms, ReceiptPrinterSettings settings)
    {
        var customText = terms.Kind switch
        {
            ReceiptTermsKind.Installment => settings.InstallmentTerms,
            ReceiptTermsKind.RefundVoucher => settings.VoucherTerms,
            _ => null
        };

        var lines = ResolveLines(customText, terms.Lines);
        return ReferenceEquals(lines, terms.Lines) ? terms : terms with { Lines = lines };
    }
}
