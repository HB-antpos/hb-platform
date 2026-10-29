using System.Globalization;
using System.Text;
using iTextSharp.text;
using iTextSharp.text.pdf;

namespace BlazorApp.Api.Services.Myob;

/// <summary>工资单展示用的小工具：Type 列文字、文件名、邮箱脱敏、日期 / 金额格式（与 MYOB 默认工资单一致）。</summary>
internal static class MyobPayslipPresentation
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>MYOB 默认工资单 TYPE 列：Wage 显示为 Wages，其余原样。</summary>
    internal static string? TypeLabel(string? type) =>
        string.IsNullOrWhiteSpace(type)
            ? null
            : type.Trim().Equals("Wage", StringComparison.OrdinalIgnoreCase) ? "Wages" : type.Trim();

    internal static bool IsEntitlement(string? type) =>
        string.Equals(type?.Trim(), "Entitlement", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Entitlement（假期累计）行的「数量」是小时数：真实响应里它落在 Amount 上（方案第 1.3 节），默认工资单里应放 HOURS 列、AMOUNT 留空。
    /// 返回 (Hours, Amount)。
    /// </summary>
    internal static (decimal? Hours, decimal? Amount) NormalizeQuantity(string? type, decimal? hours, decimal? amount) =>
        IsEntitlement(type) ? (hours ?? amount, null) : (hours, amount);

    /// <summary>d/M/yyyy，不补零（MYOB 原样，如 5/10/2026）。</summary>
    internal static string Date(DateTime? value) => value?.ToString("d/M/yyyy", Invariant) ?? string.Empty;

    /// <summary>金额：$1,234.56，负数 -$138.00；空值返回空串。</summary>
    internal static string Money(decimal? value, int decimals = 2)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var text = Math.Abs(value.Value).ToString("N" + decimals.ToString(Invariant), Invariant);
        return value.Value < 0 ? "-$" + text : "$" + text;
    }

    internal static string Number(decimal? value) =>
        value?.ToString("N2", Invariant) ?? string.Empty;

    /// <summary>Payslip_ALEX-MORGAN_2026-10-29.pdf（只留字母数字，空格变短横线）。</summary>
    internal static string Filename(string? employeeName, DateTime? paymentDate)
    {
        var builder = new StringBuilder();
        foreach (var ch in (employeeName ?? string.Empty).Trim().ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var name = builder.ToString().Trim('-');
        if (name.Length == 0)
        {
            name = "EMPLOYEE";
        }

        return $"Payslip_{name}_{(paymentDate ?? DateTime.MinValue).ToString("yyyy-MM-dd", Invariant)}.pdf";
    }

    /// <summary>a***@example.com：只露首字符与域名。</summary>
    internal static string MaskEmail(string? email)
    {
        var text = email?.Trim() ?? string.Empty;
        var at = text.IndexOf('@');
        return at <= 0 ? string.Empty : $"{text[0]}***{text[at..]}";
    }
}

/// <summary>PDF 的一行明细（已按 MYOB 默认工资单整理好列）。</summary>
internal sealed record MyobPayslipPdfLine(
    string Description, decimal? Hours, decimal? Rate, decimal? Amount, decimal? YearToDate, string TypeLabel, bool IsHoursYtd);

/// <summary>渲染 PDF 所需的全部内容（来自 MYOB 报表，姓名 / 公司名原样）。</summary>
internal sealed record MyobPayslipPdfModel(
    string CompanyName,
    string? Abn,
    string EmployeeName,
    string? ReferenceNumber,
    DateTime? PaymentDate,
    decimal? HourlyRate,
    DateTime? PeriodStart,
    DateTime? PeriodEnd,
    string? SuperFundName,
    decimal? GrossPay,
    decimal? NetPay,
    IReadOnlyList<MyobPayslipPdfLine> Lines)
{
    internal static MyobPayslipPdfModel From(MyobPayrollAdviceRecord record, string fallbackCompanyName) =>
        new(
            record.EmployerName ?? fallbackCompanyName,
            record.EmployerAbn,
            record.EmployeeName ?? string.Empty,
            record.ChequeNumber,
            record.PaymentDate,
            record.HourlyRate,
            record.PeriodStart,
            record.PeriodEnd,
            record.SuperFundName,
            record.GrossPay,
            record.NetPay,
            record.Lines.Select(line =>
            {
                var (hours, amount) = MyobPayslipPresentation.NormalizeQuantity(line.CategoryType, line.Hours, line.Amount);
                return new MyobPayslipPdfLine(
                    line.CategoryName ?? string.Empty,
                    hours,
                    line.CalculationRate,
                    amount,
                    line.YearToDate,
                    MyobPayslipPresentation.TypeLabel(line.CategoryType) ?? string.Empty,
                    MyobPayslipPresentation.IsEntitlement(line.CategoryType));
            }).ToList());
}

/// <summary>
/// 按 MYOB 默认 Pay Slip 版式生成 PDF（A4 纵向）：页头（公司、ABN、Pay Slip For、Reference Number、Payment Date、Hourly Rate、
/// Pay Period、Superannuation Fund、GROSS / NET PAY、Memo）+ 灰头通栏表（DESCRIPTION / HOURS / CALC. RATE / AMOUNT / YTD / TYPE）+ Page x of y。
/// 版式对照用户给的真实 MYOB 工资单；Memo 报表接口里没有对应字段，保留标签与空行。
/// 字体用 PDF 内建 Helvetica（CP1252）：拉丁字符完整，中文姓名无法显示（MYOB 报表里姓名本来就是英文）。
/// 行数多时表格续页，续页只重复表头。
/// </summary>
internal static class MyobPayslipPdfRenderer
{
    private const float PageW = 595.28f;
    private const float PageH = 841.89f;
    private const float Left = 42f;
    private const float Right = PageW - 42f;
    private const float RowH = 13f;
    private const float HeadH = 16f;
    private const float TableBottom = 770f;
    private const float FirstTableTop = 292f;
    private const float NextTableTop = 60f;

    // 列宽（合计 = Right - Left = 511.28）：Description 自适应，其余固定。
    private static readonly float[] FixedCols = [52f, 66f, 74f, 80f, 74f];

    private static readonly BaseColor Ink = new(0x11, 0x11, 0x11);
    private static readonly BaseColor Grid = new(0x77, 0x77, 0x77);
    private static readonly BaseColor HeadFill = new(0xD9, 0xD9, 0xD9);

    internal static byte[] Render(MyobPayslipPdfModel model)
    {
        var font = BaseFont.CreateFont(BaseFont.HELVETICA, BaseFont.CP1252, BaseFont.NOT_EMBEDDED);

        // 每页能放的行数：第一页表格从页头下面开始，续页从页顶开始。
        var firstRows = (int)Math.Floor((TableBottom - FirstTableTop - HeadH) / RowH);
        var nextRows = (int)Math.Floor((TableBottom - NextTableTop - HeadH) / RowH);
        var lines = model.Lines;
        var pages = 1;
        if (lines.Count > firstRows)
        {
            pages += (int)Math.Ceiling((lines.Count - firstRows) / (double)nextRows);
        }

        using var stream = new MemoryStream();
        using var document = new Document(PageSize.A4, 0, 0, 0, 0);
        var writer = PdfWriter.GetInstance(document, stream);
        document.AddTitle("Pay Slip");
        document.AddCreator("HB Platform");
        document.Open();
        var cb = writer.DirectContent;

        var offset = 0;
        for (var page = 1; page <= pages; page++)
        {
            if (page > 1)
            {
                document.NewPage();
            }

            var tableTop = page == 1 ? FirstTableTop : NextTableTop;
            if (page == 1)
            {
                DrawHeader(cb, font, model);
            }

            var capacity = page == 1 ? firstRows : nextRows;
            var count = Math.Min(capacity, lines.Count - offset);
            DrawTable(cb, font, tableTop, lines.Skip(offset).Take(Math.Max(0, count)).ToList());
            offset += Math.Max(0, count);

            Text(cb, font, 8.5f, Element.ALIGN_RIGHT, Right, 790f, $"Page {page} of {pages}");
        }

        document.Close();
        return stream.ToArray();
    }

    private static float Y(float top) => PageH - top;

    private static void Text(PdfContentByte cb, BaseFont font, float size, int align, float x, float top, string text)
    {
        cb.BeginText();
        cb.SetFontAndSize(font, size);
        cb.SetColorFill(Ink);
        cb.ShowTextAligned(align, text, x, Y(top), 0);
        cb.EndText();
    }

    private static void Kv(PdfContentByte cb, BaseFont font, float labelX, float valueX, float top, string label, string value)
    {
        Text(cb, font, 9f, Element.ALIGN_LEFT, labelX, top, label);
        Text(cb, font, 9f, Element.ALIGN_LEFT, valueX, top, value);
    }

    private static void DrawHeader(PdfContentByte cb, BaseFont font, MyobPayslipPdfModel m)
    {
        Text(cb, font, 15f, Element.ALIGN_LEFT, Left, 76f, m.CompanyName.ToUpperInvariant());

        Kv(cb, font, Left, 128f, 112f, "ABN.", m.Abn ?? string.Empty);
        Kv(cb, font, Left, 128f, 126f, "Pay Slip For:", m.EmployeeName);
        Kv(cb, font, 330f, 430f, 126f, "Reference Number:", m.ReferenceNumber ?? string.Empty);
        Kv(cb, font, 330f, 430f, 140f, "Payment Date:", MyobPayslipPresentation.Date(m.PaymentDate));

        const float indent = Left + 9f;
        Kv(cb, font, indent, 150f, 176f, "Hourly Rate:", m.HourlyRate is null ? string.Empty : MyobPayslipPresentation.Money(m.HourlyRate, 4));
        Kv(cb, font, indent, 150f, 202f, "Pay Period From:", MyobPayslipPresentation.Date(m.PeriodStart));
        Text(cb, font, 9f, Element.ALIGN_LEFT, 214f, 202f, "To:");
        Text(cb, font, 9f, Element.ALIGN_LEFT, 238f, 202f, MyobPayslipPresentation.Date(m.PeriodEnd));
        Kv(cb, font, indent, 150f, 228f, "Superannuation Fund:", m.SuperFundName ?? string.Empty);
        Kv(cb, font, 330f, 430f, 202f, "GROSS PAY:", MyobPayslipPresentation.Money(m.GrossPay));
        Kv(cb, font, 330f, 430f, 228f, "NET PAY:", MyobPayslipPresentation.Money(m.NetPay));

        // Memo：报表接口没有对应字段，保留标签与空行，版式与 MYOB 原版一致。
        Text(cb, font, 9f, Element.ALIGN_LEFT, indent, 258f, "Memo/payslip message:");
    }

    private static void DrawTable(PdfContentByte cb, BaseFont font, float top, IReadOnlyList<MyobPayslipPdfLine> rows)
    {
        var width = Right - Left;
        var descW = width - FixedCols.Sum();
        var widths = new[] { descW }.Concat(FixedCols).ToArray();
        var xs = new float[widths.Length + 1];
        xs[0] = Left;
        for (var i = 0; i < widths.Length; i++)
        {
            xs[i + 1] = xs[i] + widths[i];
        }

        // 表头灰底 + 外框 + 竖线贯穿到表底（原版是一块高的空表格）。
        cb.SetColorFill(HeadFill);
        cb.Rectangle(Left, Y(top + HeadH), width, HeadH);
        cb.Fill();
        cb.SetColorStroke(Grid);
        cb.SetLineWidth(0.6f);
        cb.Rectangle(Left, Y(TableBottom), width, TableBottom - top);
        cb.Stroke();
        cb.MoveTo(Left, Y(top + HeadH));
        cb.LineTo(Right, Y(top + HeadH));
        cb.Stroke();
        for (var i = 1; i < xs.Length - 1; i++)
        {
            cb.MoveTo(xs[i], Y(top));
            cb.LineTo(xs[i], Y(TableBottom));
            cb.Stroke();
        }

        var headers = new[] { "DESCRIPTION", "HOURS", "CALC. RATE", "AMOUNT", "YTD", "TYPE" };
        for (var i = 0; i < headers.Length; i++)
        {
            if (i == 0)
            {
                Text(cb, font, 8.5f, Element.ALIGN_LEFT, xs[0] + 4f, top + 11.5f, headers[i]);
            }
            else
            {
                Text(cb, font, 8.5f, Element.ALIGN_CENTER, (xs[i] + xs[i + 1]) / 2, top + 11.5f, headers[i]);
            }
        }

        var rowTop = top + HeadH;
        foreach (var row in rows)
        {
            var baseline = rowTop + 10f;
            Text(cb, font, 8.5f, Element.ALIGN_LEFT, xs[0] + 4f, baseline, Fit(font, 8.5f, row.Description, widths[0] - 8f));
            Text(cb, font, 8.5f, Element.ALIGN_RIGHT, xs[2] - 5f, baseline, MyobPayslipPresentation.Number(row.Hours));
            Text(cb, font, 8.5f, Element.ALIGN_RIGHT, xs[3] - 5f, baseline, MyobPayslipPresentation.Money(row.Rate));
            Text(cb, font, 8.5f, Element.ALIGN_RIGHT, xs[4] - 5f, baseline, MyobPayslipPresentation.Money(row.Amount));
            // Entitlement 的 YTD 是累计小时数，不带 $。
            var ytd = row.IsHoursYtd ? MyobPayslipPresentation.Number(row.YearToDate) : MyobPayslipPresentation.Money(row.YearToDate);
            Text(cb, font, 8.5f, Element.ALIGN_RIGHT, xs[5] - 5f, baseline, ytd);
            Text(cb, font, 8.5f, Element.ALIGN_CENTER, (xs[5] + xs[6]) / 2, baseline, Fit(font, 8.5f, row.TypeLabel, widths[5] - 6f));
            rowTop += RowH;
        }
    }

    /// <summary>超出列宽时截断并补省略号（避免长类别名压到下一列）。</summary>
    private static string Fit(BaseFont font, float size, string text, float maxWidth)
    {
        if (font.GetWidthPoint(text, size) <= maxWidth)
        {
            return text;
        }

        var cut = text;
        while (cut.Length > 1 && font.GetWidthPoint(cut + "...", size) > maxWidth)
        {
            cut = cut[..^1];
        }

        return cut + "...";
    }
}
