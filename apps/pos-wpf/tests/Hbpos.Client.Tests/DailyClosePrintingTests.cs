using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

// 新增的中心日志用例会替换 ConsoleLog 的全局出口，整个类必须独占运行。
[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class DailyClosePrintingTests
{
    [Fact]
    public async Task Daily_close_print_service_prints_required_daily_close_sections()
    {
        var settingsStore = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with
            {
                PrinterPort = "COM5",
                BrandName = "HotBargain",
                StoreName = "Sunnybank"
            }
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new DailyClosePrintService(settingsStore, driver);

        var result = await service.PrintAsync(CreateArchive(), ReceiptPrintReason.Manual);

        Assert.True(result.Succeeded);
        Assert.Equal("Daily close report and cash slip printed.", result.Message);
        Assert.Equal("COM5", driver.LastSettings?.PrinterPort);
        // 驱动每次打印末尾都会切纸：日结单与现金单分两次下发即两张独立纸条。
        Assert.Equal(2, driver.Documents.Count);
        var report = driver.Documents[0];
        Assert.Contains(report.PreviewRows, row => row.Text == "==== DAILY CLOSE ====" && row.IsCentered && row.IsEmphasized);
        AssertProminentBusinessDate(report, "2026-05-27 WED");
        Assert.DoesNotContain(report.PreviewRows, row => row.Text.Contains("CHECK DATE", StringComparison.Ordinal));
        Assert.Contains(report.Elements, element => element.Text == "Store: Sunnybank (S001)");
        Assert.Contains(report.PreviewRows, row => row.Text == "Store: Sunnybank (S001)");
        Assert.Contains(report.PreviewRows, row => row.Text == "Terminal: POS-01");
        Assert.Contains(report.PreviewRows, row => row.Text == "Cashier: Alice");
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("Cash Counted", StringComparison.Ordinal) && row.Text.Contains("$287.00", StringComparison.Ordinal));
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("Cash Difference", StringComparison.Ordinal) && row.Text.Contains("+$7.00", StringComparison.Ordinal));
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("Refund Amount", StringComparison.Ordinal) && row.Text.Contains("$25.50", StringComparison.Ordinal));
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("Return Qty", StringComparison.Ordinal) && row.Text.Contains("3", StringComparison.Ordinal));
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("Cash", StringComparison.Ordinal) && row.Text.Contains("$300.00", StringComparison.Ordinal));
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("Notes Total", StringComparison.Ordinal) && row.Text.Contains("$260.00", StringComparison.Ordinal));
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("Coins Total", StringComparison.Ordinal) && row.Text.Contains("$27.00", StringComparison.Ordinal));
        AssertAllDenominationsArePrinted(report);
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("$5", StringComparison.Ordinal) && row.Text.Contains("x0", StringComparison.Ordinal) && row.Text.Contains("$0.00", StringComparison.Ordinal));
        Assert.Contains(report.PreviewRows, row => row.Text.Contains("5c", StringComparison.Ordinal) && row.Text.Contains("x0", StringComparison.Ordinal) && row.Text.Contains("$0.00", StringComparison.Ordinal));

        var cashSlip = driver.Documents[1];
        Assert.Contains(cashSlip.PreviewRows, row => row.Text == "==== CASH COUNT ====" && row.IsCentered && row.IsEmphasized);
        AssertProminentBusinessDate(cashSlip, "2026-05-27 WED");
        Assert.Contains(cashSlip.PreviewRows, row => row.Text == "Terminal: POS-01");
        Assert.Contains(cashSlip.PreviewRows, row => row.Text == "Cashier: Alice");
        Assert.Contains(cashSlip.PreviewRows, row => row.Text.Contains("Notes Total", StringComparison.Ordinal) && row.Text.Contains("$260.00", StringComparison.Ordinal));
        Assert.Contains(cashSlip.PreviewRows, row => row.Text.Contains("Coins Total", StringComparison.Ordinal) && row.Text.Contains("$27.00", StringComparison.Ordinal));
        Assert.Contains(cashSlip.PreviewRows, row => row.IsEmphasized && row.Text.Contains("Cash Counted", StringComparison.Ordinal) && row.Text.Contains("$287.00", StringComparison.Ordinal));
        AssertAllDenominationsArePrinted(cashSlip);
        Assert.Contains(cashSlip.PreviewRows, row => row.Text == "KEEP THIS SLIP" && row.IsCentered && row.IsEmphasized);
        Assert.Contains(cashSlip.PreviewRows, row => row.Text == "WITH THE CASH" && row.IsCentered && row.IsEmphasized);
        Assert.Contains(cashSlip.PreviewRows, row => row.Text.StartsWith("Counted by:", StringComparison.Ordinal));
        Assert.Contains(cashSlip.PreviewRows, row => row.Text.StartsWith("Checked by:", StringComparison.Ordinal));
        // 现金单只随现金交接，不含销售汇总与长短款。
        Assert.DoesNotContain(cashSlip.PreviewRows, row => row.Text.StartsWith("Payment", StringComparison.Ordinal));
        Assert.DoesNotContain(cashSlip.PreviewRows, row => row.Text.Contains("Cash Expected", StringComparison.Ordinal));
        Assert.DoesNotContain(cashSlip.PreviewRows, row => row.Text.Contains("Cash Difference", StringComparison.Ordinal));
        Assert.All(cashSlip.PreviewRows, row => Assert.True(row.Text.Length <= 42, $"Cash slip line exceeds 42 characters: {row.Text}"));
    }

    [Fact]
    public async Task Daily_close_print_service_returns_driver_failure_message()
    {
        var driver = new RecordingReceiptPrinterDriver
        {
            PrintResult = new ReceiptPrinterDriverResult(false, "printer offline")
        };
        var service = new DailyClosePrintService(new FakeReceiptPrinterSettingsStore(), driver);

        var result = await service.PrintAsync(CreateArchive(), ReceiptPrintReason.Reprint);

        Assert.False(result.Succeeded);
        Assert.Equal("printer offline", result.Message);
        var report = Assert.Single(driver.Documents);
        Assert.Contains(report.PreviewRows, row => row.Text == "==== DAILY CLOSE REPRINT ====");
    }

    [Fact]
    public async Task Daily_close_print_service_reports_cash_slip_failure_after_the_report_printed()
    {
        var driver = new RecordingReceiptPrinterDriver
        {
            PrintResults = new Queue<ReceiptPrinterDriverResult>(
            [
                new ReceiptPrinterDriverResult(true, "printed"),
                new ReceiptPrinterDriverResult(false, "paper out")
            ])
        };
        var service = new DailyClosePrintService(new FakeReceiptPrinterSettingsStore(), driver);

        var result = await service.PrintAsync(CreateArchive(), ReceiptPrintReason.Reprint);

        Assert.False(result.Succeeded);
        Assert.Equal("Daily close report printed, but the cash slip failed: paper out", result.Message);
        Assert.Equal(2, driver.Documents.Count);
        Assert.Contains(driver.Documents[1].PreviewRows, row => row.Text == "==== CASH COUNT REPRINT ====");
    }

    [Fact]
    public async Task Daily_close_report_failure_is_logged_with_part_reason_and_archive()
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        var archive = CreateArchive();
        try
        {
            var driver = new RecordingReceiptPrinterDriver
            {
                PrintResult = new ReceiptPrinterDriverResult(false, "Printer status could not be read.")
            };
            var service = new DailyClosePrintService(new FakeReceiptPrinterSettingsStore(), driver);

            await service.PrintAsync(archive, ReceiptPrintReason.Reprint);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var entry = Assert.Single(sink.Entries, logged => logged.Category == "DailyClosePrint");
        Assert.Equal("Warning", entry.Level);
        Assert.Contains("part=report", entry.Message, StringComparison.Ordinal);
        Assert.Contains("reason=Reprint", entry.Message, StringComparison.Ordinal);
        Assert.Contains(archive.DailyCloseGuid.ToString("D"), entry.Message, StringComparison.Ordinal);
        Assert.Equal("report", entry.Properties!["part"]);
        Assert.Equal(archive.DailyCloseGuid.ToString("D"), entry.Properties["dailyCloseGuid"]);
        Assert.Equal(
            archive.Report.BusinessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            entry.Properties["businessDate"]);
        Assert.Equal("Printer status could not be read.", entry.Properties["error"]);
    }

    [Fact]
    public async Task Daily_close_cash_slip_failure_is_logged_as_the_cash_slip_part()
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            var driver = new RecordingReceiptPrinterDriver
            {
                PrintResults = new Queue<ReceiptPrinterDriverResult>(
                [
                    new ReceiptPrinterDriverResult(true, "printed"),
                    new ReceiptPrinterDriverResult(false, "paper out")
                ])
            };
            var service = new DailyClosePrintService(new FakeReceiptPrinterSettingsStore(), driver);

            await service.PrintAsync(CreateArchive(), ReceiptPrintReason.Manual);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var entry = Assert.Single(sink.Entries, logged => logged.Category == "DailyClosePrint");
        Assert.Equal("Warning", entry.Level);
        Assert.Contains("part=cash-slip", entry.Message, StringComparison.Ordinal);
        Assert.Contains("reason=Manual", entry.Message, StringComparison.Ordinal);
        // 结构化属性是中心日志按条件检索用的，不能只断言 message。
        Assert.Equal("cash-slip", entry.Properties!["part"]);
        Assert.Equal("Manual", entry.Properties["reason"]);
        Assert.Equal("paper out", entry.Properties["error"]);
    }

    [Fact]
    public async Task Daily_close_driver_exception_is_logged_as_error_with_the_exception()
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        ReceiptPrintResult result;
        try
        {
            var service = new DailyClosePrintService(new FakeReceiptPrinterSettingsStore(), new ThrowingReceiptPrinterDriver());

            result = await service.PrintAsync(CreateArchive(), ReceiptPrintReason.Manual);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        // 失败结果与原来一致：异常信息原样交给界面。
        Assert.False(result.Succeeded);
        Assert.Equal("printer.sdk.dll missing", result.Message);
        var entry = Assert.Single(sink.Entries, logged => logged.Category == "DailyClosePrint");
        Assert.Equal("Error", entry.Level);
        Assert.Equal(nameof(InvalidOperationException), entry.ExceptionType);
        Assert.Contains("part=exception", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_daily_close_print_writes_no_print_failure_log()
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            var service = new DailyClosePrintService(new FakeReceiptPrinterSettingsStore(), new RecordingReceiptPrinterDriver());

            var result = await service.PrintAsync(CreateArchive(), ReceiptPrintReason.Manual);

            Assert.True(result.Succeeded);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        Assert.DoesNotContain(sink.Entries, entry => entry.Category == "DailyClosePrint");
    }

    [Fact]
    public void Business_date_that_differs_from_the_saved_day_prints_a_check_date_warning_on_both_slips()
    {
        // 模拟 1013 事故：10-01 晚上保存，但营业日期仍是 09-30。
        var archive = CreateArchive(
            businessDate: new DateTime(2026, 9, 30),
            savedAt: new DateTimeOffset(new DateTime(2026, 10, 1, 20, 42, 15, DateTimeKind.Local)));

        foreach (var document in new[]
                 {
                     DailyCloseTextFormatter.Build(archive, ReceiptPrinterSettings.Default),
                     DailyCloseTextFormatter.BuildCashSlip(archive, ReceiptPrinterSettings.Default)
                 })
        {
            AssertProminentBusinessDate(document, "2026-09-30 WED");
            Assert.Contains(document.PreviewRows, row => row.Text == "** CHECK DATE **" && row.IsCentered && row.IsEmphasized);
            Assert.Contains(document.PreviewRows, row => row.Text == "Saved on 2026-10-01, not the business date");
            Assert.All(document.PreviewRows, row => Assert.True(row.Text.Length <= 42, $"Line exceeds 42 characters: {row.Text}"));
        }
    }

    [Fact]
    public async Task BuildDocumentAsync_builds_reprint_preview_without_printing()
    {
        var driver = new RecordingReceiptPrinterDriver();
        var settingsStore = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with { StoreName = "Sunnybank" }
        };
        var service = new DailyClosePrintService(settingsStore, driver);

        var document = await service.BuildDocumentAsync(CreateArchive(), ReceiptPrintReason.Reprint);

        Assert.Empty(driver.Documents);
        var rows = document.PreviewRows.Select(row => row.Text).ToList();
        var reportTitle = rows.IndexOf("==== DAILY CLOSE REPRINT ====");
        var cut = rows.IndexOf(DailyCloseTextFormatter.CutMarker);
        var cashTitle = rows.IndexOf("==== CASH COUNT REPRINT ====");
        Assert.True(reportTitle >= 0 && reportTitle < cut && cut < cashTitle, "预览应按日结单、切纸、现金单的顺序展示。");
        Assert.Contains(document.Elements, element => element.Text == "Store: Sunnybank (S001)");
        Assert.Contains(document.PreviewRows, row => row.Text == "Store: Sunnybank (S001)");
        AssertAllDenominationsArePrinted(document);
    }

    [Theory]
    [InlineData("   ", "Store: S001")]
    [InlineData("  s001  ", "Store: S001")]
    public void Daily_close_text_formatter_uses_store_code_without_duplicate_name(string storeName, string expected)
    {
        var settings = ReceiptPrinterSettings.Default with { StoreName = storeName };

        var document = DailyCloseTextFormatter.Build(CreateArchive(), settings);

        Assert.Contains(document.Elements, element => element.Text == expected);
    }

    [Fact]
    public void Daily_close_text_formatter_wraps_every_store_line_to_receipt_width()
    {
        var settings = ReceiptPrinterSettings.Default with
        {
            StoreName = "The Very Long Sunnybank Shopping Centre Main Store"
        };

        var document = DailyCloseTextFormatter.Build(CreateArchive(), settings);

        var storeLines = document.Elements
            .SkipWhile(element => !element.Text.StartsWith("Store: ", StringComparison.Ordinal))
            .TakeWhile(element => !element.Text.StartsWith("Terminal: ", StringComparison.Ordinal))
            .Select(element => element.Text)
            .ToList();
        Assert.NotEmpty(storeLines);
        Assert.Equal(
            "Store: The Very Long Sunnybank Shopping Centre Main Store (S001)",
            string.Join(" ", storeLines));
        Assert.All(storeLines, line => Assert.True(line.Length <= 42, $"Store line exceeds 42 characters: {line}"));
    }

    private static DailyCloseArchive CreateArchive(DateTime? businessDate = null, DateTimeOffset? savedAt = null)
    {
        var report = new DailyCloseReport(
            businessDate ?? new DateTime(2026, 5, 27),
            new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            12,
            [
                new DailyClosePaymentSummary(PaymentMethodKind.Cash, 300m, 20m, 280m, 8),
                new DailyClosePaymentSummary(PaymentMethodKind.Card, 520.50m, 0m, 520.50m, 3),
                new DailyClosePaymentSummary(PaymentMethodKind.Voucher, 40m, 5.50m, 34.50m, 1)
            ],
            25.50m,
            3m);
        var counts = new[]
        {
            Count(100m, 1),
            Count(50m, 1),
            Count(20m, 5),
            Count(10m, 1),
            Count(2m, 5),
            Count(1m, 10),
            Count(0.50m, 2),
            Count(0.20m, 5),
            Count(0.10m, 50)
        };

        return new DailyCloseArchive(
            Guid.NewGuid(),
            report,
            counts,
            savedAt ?? new DateTimeOffset(new DateTime(2026, 5, 27, 18, 30, 0, DateTimeKind.Local)),
            260m,
            27m,
            287m,
            7m);
    }

    private static CashDenominationCount Count(decimal value, int quantity)
    {
        var denomination = DailyCloseService.AustralianDenominations.Single(item => item.Value == value);
        return new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, quantity);
    }

    private static void AssertProminentBusinessDate(ReceiptPrintDocument document, string expectedDate)
    {
        var rows = document.PreviewRows.ToList();
        var label = rows.FindIndex(row => row.Text == "BUSINESS DATE" && row.IsCentered);
        Assert.True(label >= 0, "缺少营业日期标签。");
        var date = rows[label + 1];
        Assert.Equal(expectedDate, date.Text);
        Assert.True(date.IsEmphasized && date.IsCentered, "营业日期应居中加粗单独成行。");
        Assert.True(rows[label - 1].IsSeparator, "营业日期块上方应有分隔线。");
        Assert.DoesNotContain(rows, row => row.Text.StartsWith("Daily Close Date:", StringComparison.Ordinal));
    }

    private static void AssertAllDenominationsArePrinted(ReceiptPrintDocument document)
    {
        foreach (var denomination in DailyCloseService.AustralianDenominations)
        {
            Assert.Contains(document.PreviewRows, row => row.Text.Contains(denomination.Label, StringComparison.Ordinal));
        }
    }

    private sealed class FakeReceiptPrinterSettingsStore : IReceiptPrinterSettingsStore
    {
        public ReceiptPrinterSettings Settings { get; init; } = ReceiptPrinterSettings.Default;

        public Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingReceiptPrinterDriver : IReceiptPrinterDriver
    {
        public Task<ReceiptPrinterDriverResult> PrintAsync(
            ReceiptPrintDocument document,
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("printer.sdk.dll missing");

        public Task<ReceiptPrinterDriverResult> TestAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("printer.sdk.dll missing");

        public Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("printer.sdk.dll missing");
    }

    private sealed class RecordingReceiptPrinterDriver : IReceiptPrinterDriver
    {
        public List<ReceiptPrintDocument> Documents { get; } = [];

        public ReceiptPrinterSettings? LastSettings { get; private set; }

        public ReceiptPrinterDriverResult PrintResult { get; init; } = new(true, "printed");

        public Queue<ReceiptPrinterDriverResult>? PrintResults { get; init; }

        public Task<ReceiptPrinterDriverResult> PrintAsync(
            ReceiptPrintDocument document,
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            Documents.Add(document);
            LastSettings = settings;
            return Task.FromResult(PrintResults is { Count: > 0 } ? PrintResults.Dequeue() : PrintResult);
        }

        public Task<ReceiptPrinterDriverResult> TestAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "tested"));
        }

        public Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "drawer opened"));
        }
    }
}
