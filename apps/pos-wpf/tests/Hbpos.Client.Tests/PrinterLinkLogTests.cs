using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 打印机连接的中心日志：失败必须带卡住的步骤和 SDK 返回码，连续失败要累计，
/// 恢复后只记一次，平时成功不写日志（否则每张小票都会产生一条）。
/// </summary>
[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class PrinterLinkLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Failure_logs_a_warning_with_stage_sdk_result_and_port()
    {
        using var scope = new LogScope();
        var trace = new PrinterCallTrace("print", "USB,", elementCount: 42)
        {
            Stage = PrinterStages.StatusRead,
            SdkResult = -1
        };

        PrinterLinkLogState.Record(trace, new ReceiptPrinterDriverResult(false, "Printer status could not be read."), 812, T0);

        var entry = Assert.Single(scope.PrinterEntries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("Printer", entry.Category);
        Assert.Contains("printer print failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains("stage=status-read", entry.Message, StringComparison.Ordinal);
        Assert.Contains("sdkResult=-1", entry.Message, StringComparison.Ordinal);
        Assert.Contains("port=USB,", entry.Message, StringComparison.Ordinal);
        Assert.Contains("consecutiveFailures=1", entry.Message, StringComparison.Ordinal);
        var properties = entry.Properties!;
        Assert.Equal("failed", properties["status"]);
        Assert.Equal("print", properties["operation"]);
        Assert.Equal(PrinterStages.StatusRead, properties["stage"]);
        Assert.Equal(-1, properties["sdkResult"]);
        Assert.Equal("USB,", properties["port"]);
        Assert.Equal(812L, properties["elapsedMs"]);
        Assert.Equal(42, properties["elementCount"]);
        Assert.Equal("Printer status could not be read.", properties["resultMessage"]);
        // 没读到状态字节时不写 printerStatus；还没有成功过的进程也没有 lastSuccessSecondsAgo。
        Assert.DoesNotContain("printerStatus", properties.Keys);
        Assert.DoesNotContain("lastSuccessSecondsAgo", properties.Keys);
    }

    [Fact]
    public void Status_flags_failure_records_the_printer_status_byte()
    {
        using var scope = new LogScope();
        var trace = new PrinterCallTrace("cash-drawer", "USB,")
        {
            Stage = PrinterStages.StatusFlags,
            PrinterStatus = 0x42
        };

        PrinterLinkLogState.Record(trace, new ReceiptPrinterDriverResult(false, "Printer is out of paper."), 15, T0);

        var entry = Assert.Single(scope.PrinterEntries);
        Assert.Contains("printer cash-drawer failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains("stage=status-flags", entry.Message, StringComparison.Ordinal);
        Assert.Contains("printerStatus=0x42", entry.Message, StringComparison.Ordinal);
        Assert.Equal(0x42, entry.Properties!["printerStatus"]);
        Assert.DoesNotContain("elementCount", entry.Properties.Keys);
    }

    [Fact]
    public void Consecutive_failures_accumulate_with_down_time_and_last_success_age()
    {
        using var scope = new LogScope();
        PrinterLinkLogState.Record(NewTrace(), Succeeded(), 20, T0);
        PrinterLinkLogState.Record(NewTrace(), Failed(), 800, T0.AddSeconds(60));
        PrinterLinkLogState.Record(NewTrace(), Failed(), 900, T0.AddSeconds(3600));

        var entries = scope.PrinterEntries;
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].Properties!["consecutiveFailures"]);
        Assert.Equal(0L, entries[0].Properties!["downSeconds"]);
        Assert.Equal(60L, entries[0].Properties!["lastSuccessSecondsAgo"]);
        Assert.Equal(2, entries[1].Properties!["consecutiveFailures"]);
        // 停机时长从第一次失败（T0+60）算起，最后一次确认正常则是 T0。
        Assert.Equal(3540L, entries[1].Properties!["downSeconds"]);
        Assert.Equal(3600L, entries[1].Properties!["lastSuccessSecondsAgo"]);
    }

    [Fact]
    public void Recovery_logs_one_information_then_stays_quiet_and_a_later_failure_starts_counting_again()
    {
        using var scope = new LogScope();
        PrinterLinkLogState.Record(NewTrace(), Failed(), 800, T0);
        PrinterLinkLogState.Record(NewTrace(), Failed(), 800, T0.AddSeconds(10));
        PrinterLinkLogState.Record(NewTrace(), Failed(), 800, T0.AddSeconds(20));
        PrinterLinkLogState.Record(NewTrace(), Succeeded(), 120, T0.AddSeconds(300));
        PrinterLinkLogState.Record(NewTrace(), Succeeded(), 110, T0.AddSeconds(400));
        PrinterLinkLogState.Record(NewTrace(), Failed(), 700, T0.AddSeconds(500));

        var entries = scope.PrinterEntries;
        Assert.Equal(5, entries.Count);
        var recovered = Assert.Single(entries, entry => entry.Level == "Information");
        Assert.Contains("printer recovered", recovered.Message, StringComparison.Ordinal);
        Assert.Contains("failedAttempts=3", recovered.Message, StringComparison.Ordinal);
        Assert.Contains("downSeconds=300", recovered.Message, StringComparison.Ordinal);
        Assert.Equal("recovered", recovered.Properties!["status"]);
        Assert.Equal(3, recovered.Properties["failedAttempts"]);
        Assert.Equal(300L, recovered.Properties["downSeconds"]);
        // 恢复之后的成功不再记日志；之后再失败则从 1 重新计数。
        var last = entries[^1];
        Assert.Equal("Warning", last.Level);
        Assert.Equal(1, last.Properties!["consecutiveFailures"]);
        Assert.Equal(0L, last.Properties["downSeconds"]);
    }

    [Fact]
    public void Success_without_a_prior_failure_logs_nothing()
    {
        using var scope = new LogScope();

        PrinterLinkLogState.Record(NewTrace(), Succeeded(), 20, T0);
        PrinterLinkLogState.Record(NewTrace(), Succeeded(), 25, T0.AddSeconds(5));

        Assert.Empty(scope.PrinterEntries);
    }

    [Fact]
    public async Task RunAsync_returns_the_driver_result_unchanged_and_reports_the_stage_set_by_the_core()
    {
        using var scope = new LogScope();
        var trace = new PrinterCallTrace("print", "USB,");
        var failure = new ReceiptPrinterDriverResult(false, "Printer port could not be opened.");

        var result = await PrinterLinkLogState.RunAsync(
            trace,
            () =>
            {
                trace.Stage = PrinterStages.OpenPort;
                trace.SdkResult = 7;
                return failure;
            },
            CancellationToken.None);

        Assert.Same(failure, result);
        var entry = Assert.Single(scope.PrinterEntries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal(PrinterStages.OpenPort, entry.Properties!["stage"]);
        Assert.Equal(7, entry.Properties["sdkResult"]);
        Assert.IsType<long>(entry.Properties["elapsedMs"]);
    }

    [Fact]
    public async Task RunAsync_logs_an_error_with_the_exception_and_rethrows_it_unchanged()
    {
        using var scope = new LogScope();
        var trace = new PrinterCallTrace("print", "USB,");
        var thrown = new DllNotFoundException("printer.sdk.dll");

        var caught = await Assert.ThrowsAsync<DllNotFoundException>(() => PrinterLinkLogState.RunAsync(
            trace,
            () => throw thrown,
            CancellationToken.None));

        Assert.Same(thrown, caught);
        var entry = Assert.Single(scope.PrinterEntries);
        Assert.Equal("Error", entry.Level);
        Assert.Equal(nameof(DllNotFoundException), entry.ExceptionType);
        Assert.Contains("error=DllNotFoundException", entry.Message, StringComparison.Ordinal);
        Assert.Equal(1, entry.Properties!["consecutiveFailures"]);
    }

    [Fact]
    public async Task RunAsync_does_not_log_cancellation_as_a_printer_failure()
    {
        using var scope = new LogScope();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var coreRan = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrinterLinkLogState.RunAsync(
            new PrinterCallTrace("print", "USB,"),
            () =>
            {
                coreRan = true;
                return Succeeded();
            },
            cancellation.Token));

        Assert.False(coreRan);
        Assert.Empty(scope.PrinterEntries);
    }

    private static PrinterCallTrace NewTrace() => new("print", "USB,") { Stage = PrinterStages.StatusRead, SdkResult = -1 };

    private static ReceiptPrinterDriverResult Succeeded() => new(true, "Receipt printed.");

    private static ReceiptPrinterDriverResult Failed() => new(false, "Printer status could not be read.");

    /// <summary>接管 ConsoleLog 的中心日志出口，并复位打印机日志的进程级计数；释放时还原，避免污染其它测试。</summary>
    private sealed class LogScope : IDisposable
    {
        private readonly RecordingApplicationLogSink sink = new();

        public LogScope()
        {
            PrinterLinkLogState.ResetForTests();
            ConsoleLog.ConfigureCenterSink(sink);
        }

        public IReadOnlyList<ApplicationLogEntry> PrinterEntries =>
            sink.Entries.Where(entry => entry.Category == "Printer").ToArray();

        public void Dispose()
        {
            ConsoleLog.ConfigureCenterSink(null);
            PrinterLinkLogState.ResetForTests();
        }
    }
}
