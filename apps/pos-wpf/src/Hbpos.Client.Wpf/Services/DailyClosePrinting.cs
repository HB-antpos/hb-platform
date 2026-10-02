using System.Globalization;
using System.Text;

namespace Hbpos.Client.Wpf.Services;

public interface IDailyClosePrintService
{
    Task<ReceiptPrintDocument> BuildDocumentAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> PrintAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);
}

public sealed class DailyClosePrintService(
    IReceiptPrinterSettingsStore settingsStore,
    IReceiptPrinterDriver driver) : IDailyClosePrintService, IDisposable
{
    // 打印机驱动只能串行写入，日结打印和普通小票保持同样的互斥策略。
    private readonly SemaphoreSlim _printLock = new(1, 1);

    public async Task<ReceiptPrintDocument> BuildDocumentAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        // 预览按实际出纸顺序展示日结单与现金单，中间标出切纸位置。
        return DailyCloseTextFormatter.BuildPreview(archive, settings, reason);
    }

    public async Task<ReceiptPrintResult> PrintAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        await _printLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            var reportResult = await driver.PrintAsync(
                DailyCloseTextFormatter.Build(archive, settings, reason),
                settings,
                cancellationToken);
            if (!reportResult.Succeeded)
            {
                return new ReceiptPrintResult(false, reportResult.Message);
            }

            // 驱动每打完一张都会切纸；现金单单独出一张，方便随现金一起封存交接。
            var cashSlipResult = await driver.PrintAsync(
                DailyCloseTextFormatter.BuildCashSlip(archive, settings, reason),
                settings,
                cancellationToken);
            return cashSlipResult.Succeeded
                ? new ReceiptPrintResult(true, "Daily close report and cash slip printed.")
                : new ReceiptPrintResult(false, $"Daily close report printed, but the cash slip failed: {cashSlipResult.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message);
        }
        finally
        {
            _printLock.Release();
        }
    }

    public void Dispose()
    {
        _printLock.Dispose();
    }
}

public sealed class NoopDailyClosePrintService : IDailyClosePrintService
{
    public static NoopDailyClosePrintService Instance { get; } = new();

    private NoopDailyClosePrintService()
    {
    }

    public Task<ReceiptPrintDocument> BuildDocumentAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DailyCloseTextFormatter.BuildPreview(archive, ReceiptPrinterSettings.Default, reason));
    }

    public Task<ReceiptPrintResult> PrintAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, "Daily close printer is not configured."));
    }
}

public static class DailyCloseTextFormatter
{
    private const int LineWidth = 42;
    private const int PaymentLabelWidth = 14;
    private const int AmountColumnWidth = 9;
    public const string CutMarker = "- - - - - - - - CUT - - - - - - - -";

    public static ReceiptPrintDocument Build(
        DailyCloseArchive archive,
        ReceiptPrinterSettings settings,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        DateTimeOffset? printTime = null)
    {
        var report = archive.Report;
        var printedAt = (printTime ?? DateTimeOffset.Now).ToLocalTime();
        var builder = new DailyCloseDocumentBuilder();
        AppendStoreHeader(builder, settings, report.StoreCode);

        builder.Blank();
        builder.Text(GetTitle(reason), ReceiptPrintAlignment.Center, isEmphasized: true);
        AppendBusinessDate(builder, archive);
        AppendArchiveIdentity(builder, archive, settings);
        builder.Text($"Print Time: {printedAt:yyyy-MM-dd HH:mm:ss}");
        builder.Separator();
        builder.Text(FitPaymentHeader());
        builder.Separator();

        foreach (var payment in report.PaymentSummaries)
        {
            builder.Text(FitPaymentColumns(
                payment.MethodLabel,
                payment.SalesAmount,
                payment.RefundAmount,
                payment.NetAmount));
        }

        builder.Separator();
        builder.Text(FitTwoColumns("Orders", report.OrderCount.ToString(CultureInfo.InvariantCulture)));
        builder.Text(FitTwoColumns("Sales Amount", Money(report.SalesAmount)));
        builder.Text(FitTwoColumns("Refund Amount", Money(report.RefundAmount)));
        builder.Text(FitTwoColumns("Return Qty", report.ReturnQuantity.ToString("0.##", CultureInfo.InvariantCulture)));
        builder.Text(FitTwoColumns("Net Amount", Money(report.NetAmount)), isEmphasized: true);
        builder.Separator();
        builder.Text(FitTwoColumns("Cash Expected", Money(report.SystemCashAmount)));
        builder.Text(FitTwoColumns("Cash Counted", Money(archive.CountedCashAmount)), isEmphasized: true);
        builder.Text(FitTwoColumns("Cash Difference", SignedMoney(archive.CashDifference)), isEmphasized: true);

        AppendCashGroup(builder, "Notes", NormalizeCashGroup(archive.CashCounts, CashDenominationKind.Note), archive.NoteSubtotal);
        AppendCashGroup(builder, "Coins", NormalizeCashGroup(archive.CashCounts, CashDenominationKind.Coin), archive.CoinSubtotal);

        builder.Separator();
        builder.Text("END OF REPORT", ReceiptPrintAlignment.Center);
        builder.Blank();

        return builder.Build();
    }

    /// <summary>
    /// 现金单：只含点钞明细与实盘合计，单独出纸后随现金封存交接。
    /// </summary>
    public static ReceiptPrintDocument BuildCashSlip(
        DailyCloseArchive archive,
        ReceiptPrinterSettings settings,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual)
    {
        var builder = new DailyCloseDocumentBuilder();
        AppendStoreHeader(builder, settings, archive.Report.StoreCode);
        builder.Blank();
        builder.Text(GetCashSlipTitle(reason), ReceiptPrintAlignment.Center, isEmphasized: true);
        AppendBusinessDate(builder, archive);
        AppendArchiveIdentity(builder, archive, settings);

        AppendCashGroup(builder, "Notes", NormalizeCashGroup(archive.CashCounts, CashDenominationKind.Note), archive.NoteSubtotal);
        AppendCashGroup(builder, "Coins", NormalizeCashGroup(archive.CashCounts, CashDenominationKind.Coin), archive.CoinSubtotal);

        builder.Separator();
        builder.Text(FitTwoColumns("Cash Counted", Money(archive.CountedCashAmount)), isEmphasized: true);
        builder.Separator();
        builder.Text("KEEP THIS SLIP", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Text("WITH THE CASH", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Blank();
        builder.Text("Counted by: ______________________");
        builder.Blank();
        builder.Text("Checked by: ______________________");
        builder.Blank();

        return builder.Build();
    }

    /// <summary>
    /// 预览：日结单 + 切纸标记 + 现金单，与实际出纸顺序一致。
    /// </summary>
    public static ReceiptPrintDocument BuildPreview(
        DailyCloseArchive archive,
        ReceiptPrinterSettings settings,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        DateTimeOffset? printTime = null)
    {
        var report = Build(archive, settings, reason, printTime);
        var cashSlip = BuildCashSlip(archive, settings, reason);
        var cut = new ReceiptPrintElement(ReceiptPrintElementKind.Text, CutMarker, ReceiptPrintAlignment.Center);
        var cutRow = new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, CutMarker, ReceiptPrintAlignment.Center);
        return new ReceiptPrintDocument(
            [.. report.Elements, cut, .. cashSlip.Elements],
            [.. report.PreviewRows, cutRow, .. cashSlip.PreviewRows]);
    }

    private static void AppendStoreHeader(
        DailyCloseDocumentBuilder builder,
        ReceiptPrinterSettings settings,
        string storeCode)
    {
        var headerName = FirstNonBlank(settings.BrandName, settings.StoreName, storeCode);
        builder.Text(headerName, ReceiptPrintAlignment.Center, isEmphasized: true);
        if (!string.IsNullOrWhiteSpace(settings.StoreName) &&
            !string.Equals(settings.StoreName.Trim(), headerName, StringComparison.OrdinalIgnoreCase))
        {
            builder.Text(settings.StoreName.Trim(), ReceiptPrintAlignment.Center);
        }

        foreach (var addressLine in WrapByWord(settings.StoreAddress, 35))
        {
            builder.Text(addressLine, ReceiptPrintAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(settings.StorePhone))
        {
            builder.Text($"Tel: {settings.StorePhone.Trim()}", ReceiptPrintAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(settings.Abn))
        {
            builder.Text($"ABN: {settings.Abn.Trim()}", ReceiptPrintAlignment.Center);
        }
    }

    /// <summary>
    /// 营业日期单独成块并加粗；保存当天与营业日期不一致时追加核对提示（如隔夜未重启按旧日期日结）。
    /// </summary>
    private static void AppendBusinessDate(DailyCloseDocumentBuilder builder, DailyCloseArchive archive)
    {
        var businessDate = archive.Report.BusinessDate.Date;
        var savedDate = archive.SavedAt.ToLocalTime().Date;

        builder.Separator();
        builder.Text("BUSINESS DATE", ReceiptPrintAlignment.Center);
        builder.Text(FormatBusinessDate(businessDate), ReceiptPrintAlignment.Center, isEmphasized: true);
        if (savedDate != businessDate)
        {
            builder.Text("** CHECK DATE **", ReceiptPrintAlignment.Center, isEmphasized: true);
            builder.Text(
                string.Create(CultureInfo.InvariantCulture, $"Saved on {savedDate:yyyy-MM-dd}, not the business date"),
                ReceiptPrintAlignment.Center);
        }

        builder.Separator();
    }

    private static void AppendArchiveIdentity(
        DailyCloseDocumentBuilder builder,
        DailyCloseArchive archive,
        ReceiptPrinterSettings settings)
    {
        var report = archive.Report;
        builder.Text($"Archive: {archive.ShortArchiveId}");
        // 中文注释：门店名称与代码共用显示规则，并按纸宽换行，保证手动打印和重打预览一致。
        foreach (var storeLine in WrapByWord(
                     $"Store: {FormatStoreDisplay(settings.StoreName, report.StoreCode)}",
                     LineWidth))
        {
            builder.Text(storeLine);
        }

        builder.Text($"Terminal: {Fallback(report.DeviceCode)}");
        builder.Text($"Cashier: {Fallback(report.CashierName)}");
        builder.Text($"Saved: {archive.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    public static string FormatBusinessDate(DateTime businessDate)
    {
        return businessDate.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture).ToUpperInvariant();
    }

    private static void AppendCashGroup(
        DailyCloseDocumentBuilder builder,
        string title,
        IReadOnlyList<CashDenominationCount> counts,
        decimal subtotal)
    {
        builder.Separator();
        builder.Text(title, ReceiptPrintAlignment.Center, isEmphasized: true);
        foreach (var count in counts.OrderByDescending(count => count.Value))
        {
            builder.Text(FitDenominationColumns(count.Label, count.Quantity, count.Amount));
        }

        builder.Text(FitTwoColumns($"{title} Total", Money(subtotal)));
    }

    private static IReadOnlyList<CashDenominationCount> NormalizeCashGroup(
        IReadOnlyList<CashDenominationCount> counts,
        CashDenominationKind kind)
    {
        return DailyCloseService.AustralianDenominations
            .Where(denomination => denomination.Kind == kind)
            .Select(denomination =>
            {
                var count = counts.FirstOrDefault(item => item.Kind == denomination.Kind && item.Value == denomination.Value);
                return count ?? new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, 0);
            })
            .ToList();
    }

    private static string GetTitle(ReceiptPrintReason reason)
    {
        return reason switch
        {
            ReceiptPrintReason.Reprint => "==== DAILY CLOSE REPRINT ====",
            ReceiptPrintReason.Test => "==== DAILY CLOSE TEST ====",
            _ => "==== DAILY CLOSE ===="
        };
    }

    private static string GetCashSlipTitle(ReceiptPrintReason reason)
    {
        return reason switch
        {
            ReceiptPrintReason.Reprint => "==== CASH COUNT REPRINT ====",
            ReceiptPrintReason.Test => "==== CASH COUNT TEST ====",
            _ => "==== CASH COUNT ===="
        };
    }

    private static string FitPaymentHeader()
    {
        return FitColumns("Payment", "Sales", "Refund", "Net");
    }

    private static string FitPaymentColumns(string method, decimal sales, decimal refund, decimal net)
    {
        return FitColumns(method, Money(sales), Money(refund), Money(net));
    }

    private static string FitColumns(string first, string second, string third, string fourth)
    {
        return TrimTo(first, PaymentLabelWidth).PadRight(PaymentLabelWidth) +
            TrimTo(second, AmountColumnWidth).PadLeft(AmountColumnWidth) +
            TrimTo(third, AmountColumnWidth).PadLeft(AmountColumnWidth) +
            TrimTo(fourth, AmountColumnWidth + 1).PadLeft(AmountColumnWidth + 1);
    }

    private static string FitDenominationColumns(string label, int quantity, decimal amount)
    {
        var quantityText = $"x{quantity.ToString(CultureInfo.InvariantCulture)}";
        return TrimTo(label, 18).PadRight(18) +
            TrimTo(quantityText, 6).PadLeft(6) +
            Money(amount).PadLeft(18);
    }

    private static string FitTwoColumns(string left, string right)
    {
        left = TrimTo(left, 18);
        right = TrimTo(right, 20);
        return left + new string(' ', Math.Max(1, LineWidth - left.Length - right.Length)) + right;
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

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

        return string.Equals(name, code, StringComparison.OrdinalIgnoreCase)
            ? code
            : $"{name} ({code})";
    }

    private static string Fallback(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private static string Money(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }

    private static string SignedMoney(decimal amount)
    {
        var sign = amount >= 0m ? "+" : "-";
        return sign + Money(Math.Abs(amount));
    }

    private static string TrimTo(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return maxChars <= 3 ? value[..maxChars] : value[..(maxChars - 3)] + "...";
    }

    private static IReadOnlyList<string> WrapByWord(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
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

    private sealed class DailyCloseDocumentBuilder
    {
        private readonly List<ReceiptPrintElement> _elements = [];
        private readonly List<ReceiptPreviewRow> _previewRows = [];

        public void Text(
            string text,
            ReceiptPrintAlignment alignment = ReceiptPrintAlignment.Left,
            bool isEmphasized = false)
        {
            var normalized = text ?? string.Empty;
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Text, normalized, alignment, isEmphasized));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, normalized, alignment, isEmphasized));
        }

        public void Blank()
        {
            Text(string.Empty);
        }

        public void Separator()
        {
            var text = new string('-', LineWidth);
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Separator, text));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Separator, text));
        }

        public ReceiptPrintDocument Build()
        {
            return new ReceiptPrintDocument(_elements, _previewRows);
        }
    }
}
