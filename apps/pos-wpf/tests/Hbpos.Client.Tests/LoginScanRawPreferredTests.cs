using System.Windows.Input;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银员登录页「Raw Input 优先」：扫码枪给每个数字都带 Shift 时，登录框里收到的是符号串，
/// 登录页必须直接用 Raw Input 按键码识别出的条码登录，同时不能让登录框随后的回车再登录一次。
/// 全部使用假条码；用例按「新行为 / 边界 / 回归」分组，对照旧代码时新行为用例应失败、其余应通过。
/// </summary>
public sealed class LoginScanRawPreferredTests
{
    // 假条码，仅用于测试。
    private const string FakeBarcode = "5550001234567";
    private const string ScannerDevice = "fake-scanner-device";
    private const string LoginBoxSource = "login-box";
    private static readonly string LoginPage = MainViewModel.CashierLoginScannerPageId;
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    // ---------- 新行为：登录页输入框有焦点时以 Raw Input 扫码为准 ----------

    [Fact]
    public void New_login_page_with_focused_input_delivers_raw_enter_scan_to_login_handler_once()
    {
        using var logs = new ConsoleLogCapture();
        var (service, delivered, _) = CreateLoginService(textInputFocused: true);

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        Assert.Equal([FakeBarcode], delivered);
        Assert.Contains(logs.Lines, line => line.Contains(
            "raw scan preferred over focused text input barcodeInfo=length=13", StringComparison.Ordinal));
        // 日志只记长度，不记条码内容。
        Assert.DoesNotContain(logs.Lines, line => line.Contains(FakeBarcode, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void New_login_box_submission_after_preferred_raw_scan_is_suppressed_for_symbol_string_and_same_digits(
        bool boxHoldsShiftedSymbols)
    {
        using var logs = new ConsoleLogCapture();
        var (service, delivered, _) = CreateLoginService(textInputFocused: true);
        var boxText = boxHoldsShiftedSymbols ? ToUsShiftedText(FakeBarcode) : FakeBarcode;

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));
        var boxAccepted = Deduplicator(service).TryAcceptScanDelivery(boxText, LoginBoxSource, T0.AddMilliseconds(5));

        // 登录框回车是同一次扫码的另一半：不能再用符号串（或同串数字）登录第二次。
        Assert.False(boxAccepted);
        Assert.Equal([FakeBarcode], delivered);
        Assert.Contains(logs.Lines, line => line.Contains("login box submission suppressed", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(ToUsShiftedText(FakeBarcode), StringComparison.Ordinal));
    }

    [Fact]
    public void New_shift_per_digit_key_stream_logs_in_with_digits_and_ignores_the_symbol_string()
    {
        var (service, delivered, _) = CreateLoginService(textInputFocused: true);

        // 扫码枪每个数字前都带 LeftShift：Raw Input 通道按按键码识别，Shift 被忽略，得到正确数字。
        var result = TypeScan(service, ScannerDevice, FakeBarcode, T0, shiftPerDigit: true, gap: TimeSpan.FromMilliseconds(2));
        Assert.NotNull(result);
        Assert.Equal(FakeBarcode, result.Barcode);
        service.DispatchResultForDiagnostics(result);
        var boxAccepted = Deduplicator(service).TryAcceptScanDelivery(
            ToUsShiftedText(FakeBarcode),
            LoginBoxSource,
            result.CompletedAt.AddMilliseconds(3));

        Assert.Equal([FakeBarcode], delivered);
        Assert.False(boxAccepted);
    }

    [Fact]
    public void New_login_box_submission_late_inside_the_window_is_still_the_same_scan()
    {
        var (service, _, _) = CreateLoginService(textInputFocused: true);
        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        // 界面线程短暂卡顿时回车可能晚一点到，窗口内仍算同一次扫码。
        var accepted = Deduplicator(service).TryAcceptScanDelivery(ToUsShiftedText(FakeBarcode), LoginBoxSource, T0.AddMilliseconds(900));

        Assert.False(accepted);
    }

    [Fact]
    public void New_login_box_suppression_is_consumed_by_the_first_submission_only()
    {
        var (service, _, _) = CreateLoginService(textInputFocused: true);
        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));
        var deduplicator = Deduplicator(service);

        var first = deduplicator.TryAcceptScanDelivery(ToUsShiftedText(FakeBarcode), LoginBoxSource, T0.AddMilliseconds(5));
        // 只抵消一次：窗口内的下一次登录框提交是新的输入（例如扫码失败后手输），照常登录。
        var second = deduplicator.TryAcceptScanDelivery("5550009999991", LoginBoxSource, T0.AddMilliseconds(50));

        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void New_login_box_suppression_only_applies_to_the_login_box_source()
    {
        var (service, _, _) = CreateLoginService(textInputFocused: true);
        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));
        var deduplicator = Deduplicator(service);

        // 其他来源（键盘兜底、历史页搜索框等）不受登录框抵消影响，也不消耗布防。
        Assert.True(deduplicator.TryAcceptScanDelivery("5550009999991", "keyboard-fallback", T0.AddMilliseconds(5)));
        Assert.False(deduplicator.TryAcceptScanDelivery(ToUsShiftedText(FakeBarcode), LoginBoxSource, T0.AddMilliseconds(10)));
    }

    [Fact]
    public void New_first_preferred_scan_learns_the_scanner_like_an_unfocused_scan()
    {
        var binding = new FakeScannerBindingService();
        var (service, delivered, _) = CreateLoginService(textInputFocused: true, binding);

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        // 登录框默认就有焦点：若这里不学习绑定，Shift 扫码枪在全新设备上永远登录不了。
        Assert.Equal([FakeBarcode], delivered);
        Assert.Equal(ScannerDevice, binding.BoundDevicePath);
    }

    [Fact]
    public void New_main_window_login_box_submission_uses_the_shared_scan_source()
    {
        var source = ReadMainWindowSource();
        var start = source.IndexOf("private void ExecuteCashierLoginCommandFromOverlay()", StringComparison.Ordinal);
        var end = source.IndexOf("private void ClearCashierLoginOverlayInputAfterLogin()", start, StringComparison.Ordinal);
        var method = source[start..end];

        // 登录框提交与服务端的「抵消登录框回车」必须使用同一个来源常量，字面量漂移会让抵消静默失效。
        Assert.Contains("TryAcceptScanDelivery(input, RawScannerService.LoginBoxScanSource", method, StringComparison.Ordinal);
        Assert.DoesNotContain("\"login-box\"", method, StringComparison.Ordinal);
    }

    // ---------- 边界：旧代码上同样通过，用于卡住新逻辑的范围 ----------

    [Fact]
    public void Boundary_login_box_suppression_expires_so_a_later_manual_entry_logs_in()
    {
        var (service, _, _) = CreateLoginService(textInputFocused: true);
        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        var accepted = Deduplicator(service).TryAcceptScanDelivery("5550009999991", LoginBoxSource, T0.AddSeconds(5));

        Assert.True(accepted);
    }

    [Fact]
    public void Boundary_timeout_completed_scan_with_focused_input_is_left_to_the_input()
    {
        using var logs = new ConsoleLogCapture();
        var binding = new FakeScannerBindingService();
        var (service, delivered, _) = CreateLoginService(textInputFocused: true, binding);

        // 没有回车收尾的短串可能只是键盘上快速敲了几个键，登录页不能拿它去试登录。
        service.DispatchResultForDiagnostics(new RawScannerInputResult(FakeBarcode, ScannerDevice, RawScannerCompletionKind.Timeout, T0));

        Assert.Empty(delivered);
        Assert.Null(binding.BoundDevicePath);
        Assert.Contains(logs.Lines, line => line.Contains("raw scan left to focused text input", StringComparison.Ordinal));
        Assert.True(Deduplicator(service).TryAcceptScanDelivery(FakeBarcode, LoginBoxSource, T0.AddMilliseconds(5)));
    }

    [Theory]
    [InlineData("HBPOSE2-Ab_cDeFgHiJkLmNoPqRsTuVwXyZ0123456789")]
    [InlineData("EMP-0001")]
    [InlineData("5550001234ABC")]
    public void Boundary_non_digit_barcode_with_focused_input_is_left_to_the_input(string barcode)
    {
        var binding = new FakeScannerBindingService();
        var (service, delivered, _) = CreateLoginService(textInputFocused: true, binding);

        // Raw Input 按键码映射会把字母变大写、丢掉多数符号（紧急登录令牌是区分大小写的 Base64Url），这类内容只能用输入框里的原文。
        service.DispatchResultForDiagnostics(EnterScan(barcode, T0));

        Assert.Empty(delivered);
        Assert.Null(binding.BoundDevicePath);
        Assert.True(Deduplicator(service).TryAcceptScanDelivery(barcode, LoginBoxSource, T0.AddMilliseconds(5)));
    }

    // ---------- 回归：既有行为保持不变 ----------

    [Fact]
    public void Regression_login_page_without_text_input_focus_delivers_as_before_and_does_not_suppress_the_box()
    {
        var (service, delivered, _) = CreateLoginService(textInputFocused: false);

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        Assert.Equal([FakeBarcode], delivered);
        // 输入框没有焦点时登录框不会收到这次扫码，没有需要抵消的回车。
        Assert.True(Deduplicator(service).TryAcceptScanDelivery("5550009999991", LoginBoxSource, T0.AddMilliseconds(5)));
    }

    [Theory]
    [InlineData(PosTerminalViewModel.PageId)]
    [InlineData(ReceiptReturnsViewModel.PageId)]
    [InlineData(TransactionHistoryViewModel.PageId)]
    [InlineData(SpecialProductsViewModel.PageId)]
    [InlineData(DeviceRegistrationViewModel.PageId)]
    public void Regression_other_pages_with_focused_text_input_still_leave_the_scan_to_the_input(string pageId)
    {
        using var logs = new ConsoleLogCapture();
        var binding = new FakeScannerBindingService();
        var service = new RawScannerService(binding, new RawScannerInputProcessor());
        var delivered = new List<string>();
        service.Subscribe(pageId, args => delivered.Add(args.Barcode));
        service.SetActivePage(pageId);
        service.ConfigureForegroundState(() => true, () => true);

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        // 商品搜索框等：同一串按键已经进了输入框，Raw Input 再投递会重复加购。
        Assert.Empty(delivered);
        Assert.Null(binding.BoundDevicePath);
        Assert.Contains(logs.Lines, line => line.Contains("raw scan left to focused text input", StringComparison.Ordinal));
        Assert.True(Deduplicator(service).TryAcceptScanDelivery(FakeBarcode, LoginBoxSource, T0.AddMilliseconds(5)));
    }

    [Fact]
    public void Regression_manual_keyboard_typing_with_enter_still_logs_in_through_the_box()
    {
        var (service, delivered, _) = CreateLoginService(textInputFocused: true);

        // 人工逐键输入：按键间隔远超扫码超时，Raw Input 不会成串；登录框回车走原有文本提交。
        var result = TypeScan(service, "fake-keyboard", FakeBarcode, T0, shiftPerDigit: false, gap: TimeSpan.FromMilliseconds(300));
        Assert.Null(result);
        var accepted = Deduplicator(service).TryAcceptScanDelivery(FakeBarcode, LoginBoxSource, T0.AddSeconds(5));

        Assert.True(accepted);
        Assert.Empty(delivered);
    }

    [Fact]
    public async Task Regression_keys_from_a_device_other_than_the_bound_scanner_never_become_a_scan()
    {
        var binding = new FakeScannerBindingService { BoundDevicePath = ScannerDevice };
        var (service, delivered, _) = CreateLoginService(textInputFocused: true, binding);
        await service.InitializeAsync();

        var result = TypeScan(service, "fake-other-keyboard", FakeBarcode, T0, shiftPerDigit: false, gap: TimeSpan.FromMilliseconds(2));

        Assert.Null(result);
        Assert.Empty(delivered);
    }

    [Fact]
    public void Regression_background_window_without_learned_scanner_still_ignores_login_page_scans()
    {
        using var logs = new ConsoleLogCapture();
        var binding = new FakeScannerBindingService();
        var (service, delivered, _) = CreateLoginService(textInputFocused: true, binding, windowActive: false);

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        Assert.Empty(delivered);
        Assert.Null(binding.BoundDevicePath);
        Assert.Contains(logs.Lines, line => line.Contains("background raw scan ignored until scanner is learned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Regression_background_window_with_learned_scanner_still_delivers_login_page_scans()
    {
        var binding = new FakeScannerBindingService { BoundDevicePath = ScannerDevice };
        var (service, delivered, _) = CreateLoginService(textInputFocused: false, binding, windowActive: false);
        await service.InitializeAsync();

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        Assert.Equal([FakeBarcode], delivered);
        Assert.True(Deduplicator(service).TryAcceptScanDelivery("5550009999991", LoginBoxSource, T0.AddMilliseconds(5)));
    }

    [Fact]
    public void Regression_global_interceptor_still_suppresses_login_page_scan_before_delivery()
    {
        var (service, delivered, _) = CreateLoginService(textInputFocused: true);
        service.SetGlobalBarcodeInterceptor(_ => true);

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));

        Assert.Empty(delivered);
        // 被拦截的扫码没有投递，也就没有需要抵消的登录框回车。
        Assert.True(Deduplicator(service).TryAcceptScanDelivery("5550009999991", LoginBoxSource, T0.AddMilliseconds(5)));
    }

    [Fact]
    public void Regression_same_scan_from_another_source_inside_the_window_is_still_deduplicated()
    {
        var (service, delivered, _) = CreateLoginService(textInputFocused: false);

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));
        var boxAccepted = Deduplicator(service).TryAcceptScanDelivery(FakeBarcode, LoginBoxSource, T0.AddMilliseconds(100));
        var laterBoxAccepted = Deduplicator(service).TryAcceptScanDelivery(FakeBarcode, LoginBoxSource, T0.AddSeconds(2));

        // 既有的跨来源 200ms 防重复：Raw Input 与登录框对同一条码只提交一次；窗口外同一来源的再次提交保留。
        Assert.Equal([FakeBarcode], delivered);
        Assert.False(boxAccepted);
        Assert.True(laterBoxAccepted);
    }

    [Fact]
    public void Regression_consecutive_raw_scans_of_the_same_barcode_are_both_delivered()
    {
        var (service, delivered, _) = CreateLoginService(textInputFocused: false);

        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0));
        service.DispatchResultForDiagnostics(EnterScan(FakeBarcode, T0.AddMilliseconds(50)));

        // 同一来源的连续扫码必须保留，只消除跨来源的重复提交。
        Assert.Equal([FakeBarcode, FakeBarcode], delivered);
    }

    [Fact]
    public void Regression_login_overlay_scanner_page_id_stays_cashier_login()
    {
        // 登录页的扫码页标识同时是 Raw Input 优先策略的键，也出现在现场日志里（activePage=CashierLogin）。
        Assert.Equal("CashierLogin", MainViewModel.CashierLoginScannerPageId);
    }

    // ---------- 辅助 ----------

    private static (RawScannerService Service, List<string> Delivered, FakeScannerBindingService Binding) CreateLoginService(
        bool textInputFocused,
        FakeScannerBindingService? binding = null,
        bool windowActive = true)
    {
        binding ??= new FakeScannerBindingService();
        var service = new RawScannerService(binding, new RawScannerInputProcessor());
        var delivered = new List<string>();
        service.Subscribe(LoginPage, args => delivered.Add(args.Barcode));
        service.SetActivePage(LoginPage);
        service.ConfigureForegroundState(() => windowActive, () => textInputFocused);
        return (service, delivered, binding);
    }

    private static IScannerInputDeduplicator Deduplicator(RawScannerService service) => service;

    private static RawScannerInputResult EnterScan(string barcode, DateTimeOffset completedAt) =>
        new(barcode, ScannerDevice, RawScannerCompletionKind.Enter, completedAt);

    /// <summary>把数字条码还原成美式键盘布局下「每个数字都带 Shift」时输入框里得到的符号串。</summary>
    private static string ToUsShiftedText(string digits) =>
        string.Concat(digits.Select(digit => ")!@#$%^&*("[digit - '0']));

    private static RawScannerInputResult? TypeScan(
        RawScannerService service,
        string devicePath,
        string digits,
        DateTimeOffset start,
        bool shiftPerDigit,
        TimeSpan gap)
    {
        var at = start;
        foreach (var digit in digits)
        {
            if (shiftPerDigit)
            {
                Assert.Null(service.ProcessScannerKeyForDiagnostics(devicePath, Key.LeftShift, at));
            }

            Assert.Null(service.ProcessScannerKeyForDiagnostics(devicePath, (Key)((int)Key.D0 + (digit - '0')), at));
            at += gap;
        }

        return service.ProcessScannerKeyForDiagnostics(devicePath, Key.Enter, at);
    }

    private static string ReadMainWindowSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var path = Path.Combine(
                    current.FullName,
                    "apps",
                    "pos-wpf",
                    "src",
                    "Hbpos.Client.Wpf",
                    "MainWindow.xaml.cs");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate MainWindow.xaml.cs.");
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
