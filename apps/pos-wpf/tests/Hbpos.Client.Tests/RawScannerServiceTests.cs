using System.Windows.Input;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class RawScannerServiceTests
{
    [Fact]
    public void DispatchResultForDiagnostics_DispatchesToActiveHandler()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());
        RawBarcodeScannedEventArgs? received = null;
        service.Subscribe("pos", args => received = args);
        service.SetActivePage("pos");

        service.DispatchResultForDiagnostics(new RawScannerInputResult("930110", "scanner-device", RawScannerCompletionKind.Enter));

        Assert.NotNull(received);
        Assert.Equal("930110", received.Barcode);
        Assert.Contains(logs.Lines, line => line.Contains("scan accepted barcodeInfo=length=6", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("barcode=930110", StringComparison.Ordinal));
    }

    [Fact]
    public void DispatchResultForDiagnostics_suppresses_raw_delivery_after_matching_keyboard_fallback()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());
        var deliveryCount = 0;
        var now = DateTimeOffset.UtcNow;
        service.Subscribe("returns", _ => deliveryCount++);
        service.SetActivePage("returns");

        var keyboardAccepted = ((IScannerInputDeduplicator)service).TryAcceptScanDelivery(
            "ABC123",
            "keyboard-fallback",
            now);
        service.DispatchResultForDiagnostics(new RawScannerInputResult(
            "ABC123",
            "scanner-device",
            RawScannerCompletionKind.Enter,
            now.AddMilliseconds(10)));

        Assert.True(keyboardAccepted);
        Assert.Equal(0, deliveryCount);
        Assert.Contains(logs.Lines, line => line.Contains("scan duplicate suppressed source=raw", StringComparison.Ordinal));
    }

    [Fact]
    public void DispatchResultForDiagnostics_global_interceptor_suppresses_before_page_delivery_or_binding()
    {
        using var logs = new ConsoleLogCapture();
        var binding = new FakeScannerBindingService();
        var service = new RawScannerService(binding, new RawScannerInputProcessor());
        var deliveryCount = 0;
        service.Subscribe("pos", _ => deliveryCount++);
        service.SetActivePage("pos");
        service.SetGlobalBarcodeInterceptor(args =>
            args.Barcode.StartsWith("HBDEV1-", StringComparison.OrdinalIgnoreCase));

        service.DispatchResultForDiagnostics(new RawScannerInputResult(
            "hBdEv1-product-like-barcode",
            "scanner-device",
            RawScannerCompletionKind.Enter));

        Assert.Equal(0, deliveryCount);
        Assert.Null(binding.BoundDevicePath);
        Assert.Contains(logs.Lines, line =>
            line.Contains("reserved scan suppressed before active handler", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line =>
            line.Contains("hBdEv1-product-like-barcode", StringComparison.Ordinal));
    }

    [Fact]
    public void DispatchResultForDiagnostics_LogsWhenNoActiveHandler()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());
        var called = false;
        service.Subscribe("pos", _ => called = true);

        service.DispatchResultForDiagnostics(new RawScannerInputResult("930111", "scanner-device", RawScannerCompletionKind.Enter));

        Assert.False(called);
        Assert.Contains(logs.Lines, line => line.Contains("scan ignored because no active handler page=<none> barcodeInfo=length=6", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("barcode=930111", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessScannerKeyForDiagnostics_LogsBoundDeviceMismatchOnce()
    {
        using var logs = new ConsoleLogCapture();
        var binding = new FakeScannerBindingService { BoundDevicePath = "bound-scanner" };
        var service = new RawScannerService(binding, new RawScannerInputProcessor());
        await service.InitializeAsync();

        var first = service.ProcessScannerKeyForDiagnostics("other-keyboard", Key.D9, DateTimeOffset.UtcNow);
        var second = service.ProcessScannerKeyForDiagnostics("other-keyboard", Key.D3, DateTimeOffset.UtcNow);

        Assert.Null(first);
        Assert.Null(second);
        var mismatchLogs = logs.Lines
            .Where(line => line.Contains("scanner device does not match binding currentPath=other-keyboard boundPath=bound-scanner", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(mismatchLogs);
    }

    [Fact]
    public void ProcessScannerKeyForDiagnostics_LogsUnmappedKey()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());

        var result = service.ProcessScannerKeyForDiagnostics("scanner-device", Key.LeftCtrl, DateTimeOffset.UtcNow);

        Assert.Null(result);
        Assert.Contains(logs.Lines, line => line.Contains("raw input key ignored because it cannot be mapped key=LeftCtrl", StringComparison.Ordinal));
    }

    [Fact]
    public void ClearPendingInput_prevents_timeout_flush_from_dispatching_buffered_barcode()
    {
        var processor = new RawScannerInputProcessor(TimeSpan.FromMilliseconds(120), minBarcodeLength: 3);
        var service = new RawScannerService(new FakeScannerBindingService(), processor);
        var now = DateTimeOffset.UtcNow;
        var deliveryCount = 0;
        service.Subscribe("pos", _ => deliveryCount++);
        service.SetActivePage("pos");

        Assert.Null(service.ProcessScannerKeyForDiagnostics("scanner-device", Key.D9, now));
        Assert.Null(service.ProcessScannerKeyForDiagnostics("scanner-device", Key.D3, now.AddMilliseconds(10)));
        Assert.Null(service.ProcessScannerKeyForDiagnostics("scanner-device", Key.D0, now.AddMilliseconds(20)));

        service.ClearPendingInput();
        var expiredResults = processor.FlushExpired(now.AddMilliseconds(200), boundDevicePath: null);
        foreach (var result in expiredResults)
        {
            service.DispatchResultForDiagnostics(result);
        }

        Assert.Empty(expiredResults);
        Assert.Equal(0, deliveryCount);
    }

    [Fact]
    public void SetActivePage_logs_only_when_the_page_actually_changes()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());

        service.SetActivePage("dedupe-page-a");
        service.SetActivePage("dedupe-page-a");
        service.SetActivePage("dedupe-page-b");

        Assert.Single(logs.Lines, line => line.Contains("active page set page=dedupe-page-a", StringComparison.Ordinal));
        Assert.Single(logs.Lines, line => line.Contains("active page set page=dedupe-page-b", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessWindowMessage_counts_wm_input_even_before_start_and_logs_first_message_once()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());
        var handled = false;

        service.ProcessWindowMessage(IntPtr.Zero, 0x0100, IntPtr.Zero, IntPtr.Zero, ref handled);
        service.ProcessWindowMessage(IntPtr.Zero, 0x00FF, IntPtr.Zero, IntPtr.Zero, ref handled);
        service.ProcessWindowMessage(IntPtr.Zero, 0x00FF, IntPtr.Zero, IntPtr.Zero, ref handled);

        // 只统计 WM_INPUT；服务未启动时不解析原始输入，但计数照样增加，便于区分「消息没送到」与「送到被丢弃」。
        Assert.Equal(2, service.Diagnostics.WindowMessages);
        Assert.Equal(0, service.Diagnostics.Dispatched);
        Assert.Single(logs.Lines, line => line.Contains("first WM_INPUT received active=False", StringComparison.Ordinal));
        Assert.False(handled);
    }

    [Fact]
    public void Non_raw_delivery_reports_first_five_scans_then_at_most_once_per_interval()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());
        var deduplicator = (IScannerInputDeduplicator)service;
        var now = DateTimeOffset.UtcNow;

        // 现场排查：前 5 次键盘通道扫码都附带 Raw Input 计数，之后按 10 分钟限流。
        for (var index = 0; index < 5; index++)
        {
            Assert.True(deduplicator.TryAcceptScanDelivery($"EMP-{index}", "diagnostics-test-source", now.AddSeconds(index)));
        }

        Assert.True(deduplicator.TryAcceptScanDelivery("EMP-5", "diagnostics-test-source", now.AddSeconds(10)));
        Assert.True(deduplicator.TryAcceptScanDelivery("EMP-6", "diagnostics-test-source", now.AddMinutes(11)));

        var reports = logs.Lines
            .Where(line => line.Contains("non-raw scan delivered source=diagnostics-test-source", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(6, reports.Length);
        Assert.All(reports, line => Assert.Contains("threadWmInput=0", line, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("EMP-1", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessWindowMessage_counts_every_hook_message_for_liveness()
    {
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());
        var handled = false;

        service.ProcessWindowMessage(IntPtr.Zero, 0x0100, IntPtr.Zero, IntPtr.Zero, ref handled);
        service.ProcessWindowMessage(IntPtr.Zero, 0x0200, IntPtr.Zero, IntPtr.Zero, ref handled);
        service.ProcessWindowMessage(IntPtr.Zero, 0x00FF, IntPtr.Zero, IntPtr.Zero, ref handled);

        // 钩子总消息数证明窗口过程活着；WM_INPUT 单独计数。
        Assert.Equal(3, service.Diagnostics.HookMessages);
        Assert.Equal(1, service.Diagnostics.WindowMessages);
    }

    [Fact]
    public void Diagnostics_thread_level_wm_input_reports_first_and_counts_foreign_targets()
    {
        var diagnostics = new RawScannerDiagnostics();

        Assert.True(diagnostics.RecordThreadWmInput(targetsRegisteredWindow: false));
        Assert.False(diagnostics.RecordThreadWmInput(targetsRegisteredWindow: true));

        Assert.Equal(2, diagnostics.ThreadWmInput);
        Assert.Contains("threadWmInput=2 threadWmInputOtherHwnd=1", diagnostics.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_summary_reports_first_then_only_changed_counters_after_interval()
    {
        var diagnostics = new RawScannerDiagnostics();
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromMinutes(10);

        Assert.True(diagnostics.ShouldReportSummary(now, interval));
        Assert.False(diagnostics.ShouldReportSummary(now.AddMinutes(20), interval));

        diagnostics.RecordWindowMessage();
        Assert.False(diagnostics.ShouldReportSummary(now.AddMinutes(5), interval));
        Assert.True(diagnostics.ShouldReportSummary(now.AddMinutes(10), interval));
        Assert.Contains("wmInput=1", diagnostics.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_registration_state_reports_only_transitions()
    {
        var diagnostics = new RawScannerDiagnostics();

        Assert.True(diagnostics.RecordRegistrationState(true));
        Assert.False(diagnostics.RecordRegistrationState(true));
        Assert.True(diagnostics.RecordRegistrationState(false));
        Assert.False(diagnostics.RecordRegistrationState(false));
        Assert.True(diagnostics.RecordRegistrationState(true));
    }

    private sealed class FakeScannerBindingService : IScannerBindingService
    {
        public string? BoundDevicePath { get; set; }

        public Task<string?> GetBoundDevicePathAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BoundDevicePath);
        }

        public Task SetBoundDevicePathAsync(string devicePath, CancellationToken cancellationToken = default)
        {
            BoundDevicePath = devicePath;
            return Task.CompletedTask;
        }

        public Task ClearBoundDevicePathAsync(CancellationToken cancellationToken = default)
        {
            BoundDevicePath = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
