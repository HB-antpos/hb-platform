using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlazorApp.Api.Features.PosReceipts;

public sealed record PosReceiptStoreProfile(
    string StoreCode,
    string BrandName,
    string StoreName,
    string Address,
    string Phone,
    string Abn,
    string ReturnPolicy
);

public sealed record PosReceiptLineSource(
    string Name,
    string LookupCode,
    int Quantity,
    decimal ActualAmount,
    decimal DiscountAmount
);

/// <summary>Method 沿用 POSM PaymentDetail.PaymentMethod：1 现金、2 刷卡、3 代金券。</summary>
public sealed record PosReceiptPaymentSource(int Method, decimal Amount, string? CardSummary);

public sealed record PosReceiptSource(
    PosReceiptStoreProfile Store,
    string OrderGuid,
    DateTime? OrderTime,
    string CashierName,
    string DeviceCode,
    decimal TotalAmount,
    decimal DiscountAmount,
    IReadOnlyList<PosReceiptLineSource> Lines,
    IReadOnlyList<PosReceiptPaymentSource> Payments,
    IReadOnlyList<string> BankReceiptTexts
);

/// <summary>
/// 把 POS 订单按收银机（WPF ReceiptTextFormatter）的规则排成 42 列等宽小票。纯函数、无 IO，便于用固定数据逐行断言。
/// 与收银机打印对齐的约定：
/// - 抬头 / 标题 / 状态居中，订单信息左对齐，商品名独占一行，下一行是 货号 · 数量 · 金额 三列，有折扣补一行 Dis；
/// - 合计的 GST = 实收 / 11 按 2 位小数「远离零」舍入，与收银机一致；
/// - 单号条码与可读单号用订单 GUID 末 8 位（收银端「单号一维条码」改动同口径），订单行不再重复印完整 GUID；
/// - 底部只保留二维码（在线小票链接）加一行说明，Print Time 取订单时间，保证同一张小票每次打开内容一致。
/// 金额格式沿用收银机的 Money()：负数写成 "$-2.95"。
/// </summary>
public static class PosReceiptBuilder
{
    public const int LineWidth = 42;
    private const int AddressLineWidth = 35;
    public const int ShortCodeLength = 8;

    private static readonly Regex FullPanRegex = new(@"(?<!\d)(?:\d[ \t .\-]*){11,18}\d(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ReferenceLineRegex = new(
        @"^\s*(?:TXN\s*REF|RRN|RETRIEVAL\s*REF(?:ERENCE)?|STAN|TRACE(?:\s*NO)?|INVOICE(?:\s*NO)?|INV\s*NO)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>订单 GUID 去横杠后的末 8 位大写；不是合法 GUID 时按原文去横杠后取末 8 位。</summary>
    public static string CreateShortCode(string orderGuid)
    {
        var normalized = Guid.TryParse(orderGuid, out var guid)
            ? guid.ToString("N")
            : (orderGuid ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length <= ShortCodeLength
            ? normalized.ToUpperInvariant()
            : normalized[^ShortCodeLength..].ToUpperInvariant();
    }

    public static PosReceiptDocumentDto Build(PosReceiptSource source, string? shareUrl, bool includeBankReceipt)
    {
        var lines = new List<PosReceiptLineDto>();
        void Text(string text, string align = "left", bool bold = false) => lines.Add(new("text", text, align, bold));
        void Blank() => lines.Add(new("blank", string.Empty));
        void Separator() => lines.Add(new("separator", new string('-', LineWidth)));

        var store = source.Store;
        var shortCode = CreateShortCode(source.OrderGuid);
        var brandName = FirstNonBlank(store.BrandName, store.StoreName, store.StoreCode);
        Text(brandName, "center", bold: true);
        if (!string.IsNullOrWhiteSpace(store.StoreName)
            && !string.Equals(store.StoreName.Trim(), brandName, StringComparison.OrdinalIgnoreCase))
        {
            Text(store.StoreName.Trim(), "center");
        }

        foreach (var addressLine in WrapByWord(store.Address, AddressLineWidth))
        {
            Text(addressLine, "center");
        }

        if (!string.IsNullOrWhiteSpace(store.Phone))
        {
            Text($"Tel: {store.Phone.Trim()}", "center");
        }

        if (!string.IsNullOrWhiteSpace(store.Abn))
        {
            Text($"ABN: {store.Abn.Trim()}", "center");
        }

        // 与收银机一致：按净额判断，净额为负是退款单。
        var net = source.TotalAmount - Math.Abs(source.DiscountAmount);
        var isRefund = net < 0m;

        Blank();
        Text("===== TAX INVOICE =====", "center");
        Blank();
        Text(isRefund ? "*** Refund ***" : "*** Paid ***", "center", bold: true);
        Blank();

        // 单号：条码块（Code128，内容为末 8 位）+ 下方可读单号，取代原来的 "Order: <完整 GUID>" 文本行。
        lines.Add(new("barcode", shortCode, "center"));
        Text(shortCode, "center", bold: true);
        Text($"Date: {FormatDate(source.OrderTime)}");
        Text($"Cashier: {source.CashierName}");
        foreach (var storeLine in WrapByWord(
                     $"Store: {FormatStoreDisplay(store.StoreName, store.StoreCode)}", LineWidth))
        {
            Text(storeLine);
        }

        Text($"Device: {source.DeviceCode}");

        Separator();
        Text(FitColumns("ITEM", "QTY", "PRICE", 25, 5, 12));
        Separator();

        foreach (var line in source.Lines)
        {
            foreach (var nameLine in WrapByWord(line.Name, LineWidth))
            {
                Text(nameLine);
            }

            Text(FitColumns(
                TrimTo(line.LookupCode, 25),
                line.Quantity.ToString(CultureInfo.InvariantCulture),
                Money(line.ActualAmount),
                25,
                5,
                12));

            if (line.DiscountAmount != 0m)
            {
                Text(FitTwoColumns("Dis", $"-{Money(Math.Abs(line.DiscountAmount))}"));
            }
        }

        Separator();
        Text(FitTwoColumns("Subtotal", Money(source.TotalAmount)));
        if (source.DiscountAmount != 0m)
        {
            Text(FitTwoColumns("Discount", $"-{Money(Math.Abs(source.DiscountAmount))}"));
        }

        var gst = decimal.Round(net / 11m, 2, MidpointRounding.AwayFromZero);
        Text(FitTwoColumns("GST", Money(gst)));
        Text(FitTwoColumns("Total(inc GST)", Money(net)), bold: true);
        Separator();
        Text("Payment:");

        foreach (var payment in source.Payments)
        {
            Text(FitTwoColumns(PaymentLabel(payment.Method), Money(payment.Amount)));
            if (!string.IsNullOrWhiteSpace(payment.CardSummary))
            {
                Text($"  {payment.CardSummary.Trim()}");
            }
        }

        // 银行回单原文只给员工端看：公开链接任何人拿到都能打开，不放卡交易回单。
        var bankTexts = includeBankReceipt
            ? source.BankReceiptTexts.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text.Trim()).ToList()
            : [];
        if (bankTexts.Count > 0)
        {
            Separator();
            foreach (var bankText in bankTexts)
            {
                foreach (var line in bankText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    Text(SanitizeBankText(line));
                }

                Blank();
            }
        }

        if (!string.IsNullOrWhiteSpace(store.ReturnPolicy))
        {
            Separator();
            Text("Refunds and returns", "center", bold: true);
            foreach (var line in WrapByDisplayWidth(store.ReturnPolicy, LineWidth))
            {
                Text(line, "center");
            }
        }

        Separator();
        if (!string.IsNullOrWhiteSpace(shareUrl))
        {
            lines.Add(new("qr", shareUrl, "center"));
            Text("Scan to view this receipt online", "center", bold: true);
        }

        Text($"Print Time: {FormatDate(source.OrderTime)}");
        Text($"Device: {source.DeviceCode}");
        Blank();
        Text("Thank you for your purchase!", "center", bold: true);
        Blank();

        return new PosReceiptDocumentDto(source.OrderGuid, shortCode, isRefund ? "refund" : "paid", lines);
    }

    /// <summary>与收银机 LinklyBankReceiptTextSanitizer 同一规则：整串卡号（12–19 位）只留末 4 位；交易参考号等行原样保留。</summary>
    public static string SanitizeBankText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (ReferenceLineRegex.IsMatch(text))
        {
            return text;
        }

        return FullPanRegex.Replace(text, match =>
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            return digits.Length is >= 12 and <= 19 ? "****" + digits[^4..] : match.Value;
        });
    }

    /// <summary>刷卡行的卡摘要：类型 + 打码卡号末 4 位，如 "VISA ****4321"；两者都没有则为空。</summary>
    public static string? BuildCardSummary(string? cardType, string? cardNumber)
    {
        var type = string.IsNullOrWhiteSpace(cardType) ? null : cardType.Trim();
        var digits = new string((cardNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        var masked = digits.Length >= 4 ? "****" + digits[^4..] : null;
        return (type, masked) switch
        {
            (null, null) => null,
            (null, _) => masked,
            (_, null) => type,
            _ => $"{type} {masked}",
        };
    }

    private static string PaymentLabel(int method) => method switch
    {
        1 => "Cash",
        2 => "Card",
        3 => "Voucher",
        _ => $"Method {method}",
    };

    private static string FormatDate(DateTime? time) =>
        time is null ? "-" : time.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FirstNonBlank(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatStoreDisplay(string? storeName, string? storeCode)
    {
        var name = storeName?.Trim() ?? string.Empty;
        var code = storeCode?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return code.Length == 0 ? "-" : code;
        }

        if (code.Length == 0)
        {
            return name;
        }

        return string.Equals(name, code, StringComparison.OrdinalIgnoreCase) ? code : $"{name} ({code})";
    }

    private static string Money(decimal amount) =>
        string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");

    private static string TrimTo(string? value, int maxChars)
    {
        var text = value ?? string.Empty;
        if (text.Length <= maxChars)
        {
            return text;
        }

        return maxChars <= 3 ? text[..maxChars] : text[..(maxChars - 3)] + "...";
    }

    private static string FitColumns(string left, string middle, string right, int leftWidth, int middleWidth, int rightWidth) =>
        TrimTo(left, leftWidth).PadRight(leftWidth)
        + TrimTo(middle, middleWidth).PadLeft(middleWidth)
        + TrimTo(right, rightWidth).PadLeft(rightWidth);

    private static string FitTwoColumns(string left, string right)
    {
        left = TrimTo(left, 24);
        right = TrimTo(right, 16);
        return left + new string(' ', Math.Max(1, LineWidth - left.Length - right.Length)) + right;
    }

    /// <summary>按词换行；超长单词按字符硬切。与收银机 WrapByWord 一致。</summary>
    private static IReadOnlyList<string> WrapByWord(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = new StringBuilder();
            foreach (var word in words)
            {
                if (word.Length > maxChars)
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    for (var index = 0; index < word.Length; index += maxChars)
                    {
                        lines.Add(word.Substring(index, Math.Min(maxChars, word.Length - index)));
                    }

                    continue;
                }

                var nextLength = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
                if (nextLength > maxChars)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
        }

        return lines;
    }

    /// <summary>按显示宽度换行（中日韩字符占 2 列），超长词逐字符硬切。与收银机 WrapByDisplayWidth 一致。</summary>
    private static IReadOnlyList<string> WrapByDisplayWidth(string? text, int maxColumns)
    {
        if (string.IsNullOrWhiteSpace(text) || maxColumns <= 0)
        {
            return [];
        }

        var lines = new List<string>();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\t', ' ');
        foreach (var paragraph in normalized.Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = new StringBuilder();
            var currentWidth = 0;

            foreach (var word in words)
            {
                var wordWidth = DisplayWidth(word);
                if (currentWidth > 0 && currentWidth + 1 + wordWidth <= maxColumns)
                {
                    current.Append(' ').Append(word);
                    currentWidth += 1 + wordWidth;
                    continue;
                }

                if (currentWidth > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    currentWidth = 0;
                }

                if (wordWidth <= maxColumns)
                {
                    current.Append(word);
                    currentWidth = wordWidth;
                    continue;
                }

                foreach (var rune in word.EnumerateRunes())
                {
                    var runeWidth = DisplayWidth(rune);
                    if (currentWidth > 0 && currentWidth + runeWidth > maxColumns)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        currentWidth = 0;
                    }

                    current.Append(rune.ToString());
                    currentWidth += runeWidth;
                }
            }

            if (currentWidth > 0)
            {
                lines.Add(current.ToString());
            }
        }

        return lines;
    }

    private static int DisplayWidth(string value) => value.EnumerateRunes().Sum(DisplayWidth);

    private static int DisplayWidth(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x1100 and <= 0x115F
            or 0x2329 or 0x232A
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD
            ? 2
            : 1;
    }
}
