using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Hbpos.Client.Wpf.Services;

public interface IRawScannerService : IDisposable
{
    bool IsActive { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler);

    void Unsubscribe(string pageId);

    void SetActivePage(string? pageId);

    void SetGlobalBarcodeInterceptor(Func<RawBarcodeScannedEventArgs, bool>? interceptor)
    {
    }

    void Start(IntPtr hwnd);

    void Stop();

    void ClearPendingInput();

    Task ResetBindingAsync(CancellationToken cancellationToken = default);

    IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled);
}

public sealed class RawBarcodeScannedEventArgs(string barcode, string devicePath, DateTimeOffset scannedAt) : EventArgs
{
    public string Barcode { get; } = barcode;

    public string DevicePath { get; } = devicePath;

    public DateTimeOffset ScannedAt { get; } = scannedAt;
}

internal interface IScannerInputDeduplicator
{
    bool TryAcceptScanDelivery(string barcode, string source, DateTimeOffset timestamp);
}

internal sealed class ScannerInputDuplicateGuard
{
    private static readonly TimeSpan CrossSourceDuplicateWindow = TimeSpan.FromMilliseconds(200);
    private readonly object _gate = new();
    private string? _lastBarcode;
    private string? _lastSource;
    private DateTimeOffset _lastAcceptedAt = DateTimeOffset.MinValue;

    public bool TryAccept(string barcode, string source, DateTimeOffset timestamp)
    {
        var normalizedBarcode = barcode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBarcode))
        {
            return true;
        }

        lock (_gate)
        {
            var isCrossSourceDuplicate =
                !string.Equals(_lastSource, source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_lastBarcode, normalizedBarcode, StringComparison.OrdinalIgnoreCase) &&
                (timestamp - _lastAcceptedAt).Duration() <= CrossSourceDuplicateWindow;
            if (isCrossSourceDuplicate)
            {
                return false;
            }

            // 同一来源的连续扫码必须保留；只消除 Raw Input 与键盘兜底产生的同一次跨来源提交。
            _lastBarcode = normalizedBarcode;
            _lastSource = source;
            _lastAcceptedAt = timestamp;
            return true;
        }
    }
}

public sealed class RawScannerService(
    IScannerBindingService bindingService,
    RawScannerInputProcessor inputProcessor) : IRawScannerService, IScannerInputDeduplicator
{
    private const int RidInput = 0x10000003;
    private const int RidiDevicename = 0x20000007;
    private const int RIM_TYPEKEYBOARD = 1;
    private const int WM_INPUT = 0x00FF;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private static readonly TimeSpan DiagnosticsInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DiagnosticsSummaryInterval = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Action<RawBarcodeScannedEventArgs>> _handlers = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly ScannerInputDuplicateGuard _duplicateGuard = new();
    private readonly DispatcherTimer _diagnosticsTimer = new() { Interval = DiagnosticsInterval };
    private readonly RawScannerDiagnostics _diagnostics = new();
    private IntPtr _registeredHwnd;
    private string? _activePageId;
    private Func<RawBarcodeScannedEventArgs, bool>? _globalBarcodeInterceptor;
    private string? _boundDevicePath;
    private string? _lastRejectedDevicePath;
    private Key? _lastUnmappedKey;
    private bool _isBinding;
    private bool _isInitialized;
    private bool _loggedEmptyDevicePath;

    public bool IsActive { get; private set; }

    bool IScannerInputDeduplicator.TryAcceptScanDelivery(
        string barcode,
        string source,
        DateTimeOffset timestamp)
    {
        // 键盘通道收到扫码时附带一份 Raw Input 计数：两边对照能直接看出 WM_INPUT 是否送达本窗口。
        if (_diagnostics.ShouldReportNonRawDelivery(timestamp, DiagnosticsSummaryInterval))
        {
            ConsoleLog.Write("RawScanner", $"non-raw scan delivered source={source} active={IsActive} {_diagnostics.Describe()} boundDevice={!string.IsNullOrWhiteSpace(_boundDevicePath)}");
        }

        return _duplicateGuard.TryAccept(barcode, source, timestamp);
    }

    internal RawScannerDiagnostics Diagnostics => _diagnostics;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _boundDevicePath = await bindingService.GetBoundDevicePathAsync(cancellationToken);
        _isInitialized = true;
        if (string.IsNullOrWhiteSpace(_boundDevicePath))
        {
            ConsoleLog.Write("RawScanner", "no scanner device is bound; first valid POS scan will be learned");
        }
        else
        {
            ConsoleLog.Write("RawScanner", $"loaded bound scanner device path={_boundDevicePath}");
        }
    }

    public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
    {
        _handlers[pageId] = handler;
        ConsoleLog.Write("RawScanner", $"handler subscribed page={pageId}");
    }

    public void Unsubscribe(string pageId)
    {
        _handlers.Remove(pageId);
        ConsoleLog.Write("RawScanner", $"handler unsubscribed page={pageId}");
    }

    public void SetActivePage(string? pageId)
    {
        if (string.Equals(_activePageId, pageId, StringComparison.Ordinal))
        {
            // 会话、联网状态变化也会刷新扫码页；页面没变时不重复记日志。
            return;
        }

        _activePageId = pageId;
        ConsoleLog.Write("RawScanner", $"active page set page={pageId ?? "<none>"}");
    }

    public void SetGlobalBarcodeInterceptor(Func<RawBarcodeScannedEventArgs, bool>? interceptor)
    {
        _globalBarcodeInterceptor = interceptor;
    }

    public void Start(IntPtr hwnd)
    {
        if (IsActive || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_isInitialized)
        {
            ConsoleLog.Write("RawScanner", "scanner service started before binding initialization completed");
        }

        if (!TryRegisterKeyboard(hwnd, out var registerError))
        {
            ConsoleLog.WriteWarning("RawScanner", $"RegisterRawInputDevices failed error={registerError}");
            return;
        }

        _flushTimer.Tick += OnFlushTimerTick;
        _flushTimer.Start();
        _registeredHwnd = hwnd;
        IsActive = true;
        // 线程级过滤器在窗口过程之前看到 Dispatcher 取出的每条消息：用来区分「系统没投递 WM_INPUT」与「投递了但没到窗口钩子」。
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
        var windowThreadId = GetWindowThreadProcessId(hwnd, out _);
        var registeringThreadId = GetCurrentThreadId();
        ConsoleLog.Write(
            "RawScanner",
            $"raw input scanner service started hwnd=0x{hwnd.ToInt64():X} process64={Environment.Is64BitProcess} os64={Environment.Is64BitOperatingSystem} " +
            $"registeringThread={registeringThreadId} windowThread={windowThreadId} sameThread={registeringThreadId == windowThreadId}");
        VerifyRegistration("start");
        _diagnosticsTimer.Tick += OnDiagnosticsTimerTick;
        _diagnosticsTimer.Start();
    }

    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        _flushTimer.Stop();
        _flushTimer.Tick -= OnFlushTimerTick;
        _diagnosticsTimer.Stop();
        _diagnosticsTimer.Tick -= OnDiagnosticsTimerTick;
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        inputProcessor.Clear();
        IsActive = false;
        ConsoleLog.Write("RawScanner", "raw input scanner service stopped");
    }

    public void ClearPendingInput()
    {
        // 关键逻辑：仅丢弃尚未完成的扫码字符，不改变设备绑定和服务启停状态。
        inputProcessor.Clear();
    }

    public async Task ResetBindingAsync(CancellationToken cancellationToken = default)
    {
        inputProcessor.Clear();
        _boundDevicePath = null;
        await bindingService.ClearBoundDevicePathAsync(cancellationToken);
        ConsoleLog.Write("RawScanner", "scanner device binding cleared; next valid POS scan will be learned");
    }

    public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _diagnostics.RecordHookMessage();
        if (msg != WM_INPUT)
        {
            return IntPtr.Zero;
        }

        if (_diagnostics.RecordWindowMessage())
        {
            // 生产上从未见过 Raw Input 扫码记录；首条 WM_INPUT 到达即留痕，用来区分「消息没送到」与「送到但被丢弃」。
            ConsoleLog.Write("RawScanner", $"first WM_INPUT received active={IsActive} hwnd=0x{hwnd.ToInt64():X}");
        }

        if (!IsActive)
        {
            return IntPtr.Zero;
        }

        ProcessRawInput(lParam);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        var size = 0u;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        _ = GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
        {
            _diagnostics.RecordReadFailure();
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) != size)
            {
                _diagnostics.RecordReadFailure();
                return;
            }

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (raw.header.dwType != RIM_TYPEKEYBOARD ||
                raw.keyboard.Message is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            {
                return;
            }

            _diagnostics.RecordKeyDown();
            var devicePath = GetDevicePath(raw.header.hDevice);
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                _diagnostics.RecordEmptyDevicePath();
                LogEmptyDevicePath();
                return;
            }

            var timestamp = DateTimeOffset.Now;
            var key = KeyInterop.KeyFromVirtualKey(raw.keyboard.VKey);
            var result = ProcessScannerKey(devicePath, key, timestamp);

            if (result is not null)
            {
                DispatchResult(result);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal RawScannerInputResult? ProcessScannerKeyForDiagnostics(string devicePath, Key key, DateTimeOffset timestamp)
    {
        return ProcessScannerKey(devicePath, key, timestamp);
    }

    internal void DispatchResultForDiagnostics(RawScannerInputResult result)
    {
        DispatchResult(result);
    }

    private RawScannerInputResult? ProcessScannerKey(string devicePath, Key key, DateTimeOffset timestamp)
    {
        if (!RawScannerInputProcessor.CanAcceptDevice(devicePath, _boundDevicePath))
        {
            _diagnostics.RecordRejectedDevice();
            LogRejectedDevice(devicePath);
            return null;
        }

        _lastRejectedDevicePath = null;
        if (key == Key.Enter)
        {
            return inputProcessor.ProcessEnter(devicePath, timestamp, _boundDevicePath);
        }

        if (TryMapCharacter(key, out var character))
        {
            _lastUnmappedKey = null;
            return inputProcessor.ProcessCharacter(devicePath, character, timestamp, _boundDevicePath);
        }

        LogUnmappedKey(key, devicePath);
        return null;
    }

    private void OnFlushTimerTick(object? sender, EventArgs e)
    {
        foreach (var result in inputProcessor.FlushExpired(DateTimeOffset.Now, _boundDevicePath))
        {
            DispatchResult(result);
        }
    }

    private void DispatchResult(RawScannerInputResult result)
    {
        if (_activePageId is null || !_handlers.TryGetValue(_activePageId, out var handler))
        {
            ConsoleLog.Write("RawScanner", $"scan ignored because no active handler page={_activePageId ?? "<none>"} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(result.Barcode)}");
            return;
        }

        var dispatchAt = DateTimeOffset.Now;
        var completedAt = result.CompletedAt == default ? dispatchAt : result.CompletedAt;
        var scannedEvent = new RawBarcodeScannedEventArgs(result.Barcode, result.DevicePath, completedAt);
        if (_globalBarcodeInterceptor?.Invoke(scannedEvent) == true)
        {
            ConsoleLog.Write(
                "RawScanner",
                $"reserved scan suppressed before active handler barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(result.Barcode)} activePage={_activePageId}");
            return;
        }

        if (string.IsNullOrWhiteSpace(_boundDevicePath))
        {
            _boundDevicePath = result.DevicePath;
            _ = PersistBoundDevicePathAsync(result.DevicePath);
        }

        if (!_duplicateGuard.TryAccept(result.Barcode, "raw", completedAt))
        {
            ConsoleLog.Write(
                "RawScanner",
                $"scan duplicate suppressed source=raw barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(result.Barcode)} activePage={_activePageId}");
            return;
        }

        _diagnostics.RecordDispatched();
        var dispatchDelayMs = Math.Max(0, (dispatchAt - completedAt).TotalMilliseconds);
        ConsoleLog.Write(
            "RawScanner",
            $"scan accepted barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(result.Barcode)} completion={result.CompletionKind} activePage={_activePageId} dispatchDelayMs={dispatchDelayMs:0.###}");
        handler(scannedEvent);
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message != WM_INPUT)
        {
            return;
        }

        if (_diagnostics.RecordThreadWmInput(msg.hwnd == _registeredHwnd))
        {
            // 只观察不处理（不改 handled）：首条线程级 WM_INPUT 留痕，带目标窗口是否为登记窗口。
            ConsoleLog.Write(
                "RawScanner",
                $"first thread-level WM_INPUT seen targetHwnd=0x{msg.hwnd.ToInt64():X} registeredHwnd=0x{_registeredHwnd.ToInt64():X} hookWmInput={_diagnostics.WindowMessages}");
        }
    }

    private void OnDiagnosticsTimerTick(object? sender, EventArgs e)
    {
        VerifyRegistration("periodic");
        if (_diagnostics.ShouldReportSummary(DateTimeOffset.Now, DiagnosticsSummaryInterval))
        {
            ConsoleLog.Write(
                "RawScanner",
                $"raw input stats {_diagnostics.Describe()} boundDevice={!string.IsNullOrWhiteSpace(_boundDevicePath)} activePage={_activePageId ?? "<none>"}");
        }
    }

    /// <summary>
    /// 核对本进程的键盘 Raw Input 注册仍指向本窗口且带 INPUTSINK；被覆盖或丢失时记 Warning 并重新注册一次。
    /// 每个进程每个 usage 只能有一个注册目标，后注册者会悄悄顶替前者。
    /// </summary>
    private void VerifyRegistration(string trigger)
    {
        if (!IsActive || _registeredHwnd == IntPtr.Zero)
        {
            return;
        }

        var registration = TryGetKeyboardRegistration(out var queryError);
        var healthy = registration is { } current &&
            current.hwndTarget == _registeredHwnd &&
            (current.dwFlags & RIDEV_INPUTSINK) != 0;
        if (!_diagnostics.RecordRegistrationState(healthy))
        {
            return;
        }

        if (healthy)
        {
            ConsoleLog.Write("RawScanner", $"raw input registration verified trigger={trigger} hwnd=0x{_registeredHwnd.ToInt64():X}");
            return;
        }

        var description = registration is { } found
            ? $"registeredTarget=0x{found.hwndTarget.ToInt64():X} flags=0x{found.dwFlags:X}"
            : queryError is null ? "registeredTarget=<none>" : $"queryError={queryError}";
        var reRegistered = TryRegisterKeyboard(_registeredHwnd, out var registerError);
        ConsoleLog.WriteWarning(
            "RawScanner",
            $"raw input registration missing or overridden trigger={trigger} expectedTarget=0x{_registeredHwnd.ToInt64():X} {description} reRegistered={reRegistered} error={registerError?.ToString() ?? "-"}",
            CreateDiagnosticsContext("registration-lost"));
    }

    private static ApplicationLogContext CreateDiagnosticsContext(string action) =>
        new(Properties: new Dictionary<string, object?> { ["action"] = action });

    private void LogEmptyDevicePath()
    {
        if (_loggedEmptyDevicePath)
        {
            return;
        }

        _loggedEmptyDevicePath = true;
        ConsoleLog.Write("RawScanner", "raw input ignored because device path is empty");
    }

    private void LogRejectedDevice(string devicePath)
    {
        if (string.Equals(_lastRejectedDevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastRejectedDevicePath = devicePath;
        ConsoleLog.Write(
            "RawScanner",
            $"raw input ignored because scanner device does not match binding currentPath={devicePath} boundPath={_boundDevicePath ?? "<none>"}; use Reset scanner binding and scan once to learn the scanner");
    }

    private void LogUnmappedKey(Key key, string devicePath)
    {
        if (_lastUnmappedKey == key)
        {
            return;
        }

        _lastUnmappedKey = key;
        ConsoleLog.Write("RawScanner", $"raw input key ignored because it cannot be mapped key={key} devicePath={devicePath}");
    }

    private async Task PersistBoundDevicePathAsync(string devicePath)
    {
        if (_isBinding)
        {
            return;
        }

        _isBinding = true;
        try
        {
            await bindingService.SetBoundDevicePathAsync(devicePath);
            ConsoleLog.Write("RawScanner", $"scanner device learned path={devicePath}");
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("RawScanner", $"scanner device binding failed error={ex.Message}");
        }
        finally
        {
            _isBinding = false;
        }
    }

    private static string? GetDevicePath(IntPtr deviceHandle)
    {
        var size = 0u;
        _ = GetRawInputDeviceInfo(deviceHandle, RidiDevicename, IntPtr.Zero, ref size);
        if (size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            return GetRawInputDeviceInfo(deviceHandle, RidiDevicename, buffer, ref size) == uint.MaxValue
                ? null
                : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryRegisterKeyboard(IntPtr hwnd, out int? error)
    {
        var devices = new RAWINPUTDEVICE[]
        {
            new()
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        if (RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            error = null;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    private static RAWINPUTDEVICE? TryGetKeyboardRegistration(out int? error)
    {
        error = null;
        var count = 0u;
        var entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
        if (GetRegisteredRawInputDevices(null, ref count, entrySize) == uint.MaxValue &&
            Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        if (count == 0)
        {
            return null;
        }

        var devices = new RAWINPUTDEVICE[count];
        if (GetRegisteredRawInputDevices(devices, ref count, entrySize) == uint.MaxValue)
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        foreach (var device in devices.Take((int)count))
        {
            if (device.usUsagePage == 0x01 && device.usUsage == 0x06)
            {
                return device;
            }
        }

        return null;
    }

    private static bool TryMapCharacter(Key key, out char character)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            character = (char)('0' + (key - Key.D0));
            return true;
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            character = (char)('0' + (key - Key.NumPad0));
            return true;
        }

        if (key >= Key.A && key <= Key.Z)
        {
            character = (char)('A' + (key - Key.A));
            return true;
        }

        character = key switch
        {
            Key.OemMinus or Key.Subtract => '-',
            Key.OemPlus or Key.Add => '+',
            Key.OemPeriod or Key.Decimal => '.',
            Key.OemComma => ',',
            Key.Space => ' ',
            _ => '\0'
        };

        return character != '\0';
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public int dwType;
        public int dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWKEYBOARD keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices(
        [Out] RAWINPUTDEVICE[]? pRawInputDevices,
        ref uint puiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        int uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        int uiCommand,
        IntPtr pData,
        ref uint pcbSize);
}

/// <summary>
/// Raw Input 通道的进程内计数，只用于诊断日志；所有入口都在 UI 线程（WM_INPUT、DispatcherTimer、PreviewKeyDown）。
/// </summary>
internal sealed class RawScannerDiagnostics
{
    private long _windowMessages;
    private long _hookMessages;
    private long _threadWmInput;
    private long _threadWmInputOtherHwnd;
    private int _nonRawReports;
    private long _keyDowns;
    private long _readFailures;
    private long _emptyDevicePaths;
    private long _rejectedDevices;
    private long _dispatched;
    private bool? _registrationHealthy;
    private string? _lastSummary;
    private DateTimeOffset _lastSummaryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNonRawReportAt = DateTimeOffset.MinValue;

    public long WindowMessages => _windowMessages;

    public long Dispatched => _dispatched;

    /// <summary>返回 true 表示这是本进程收到的第一条 WM_INPUT。</summary>
    public bool RecordWindowMessage() => ++_windowMessages == 1;

    public void RecordHookMessage() => _hookMessages++;

    /// <summary>返回 true 表示这是本进程在线程消息循环里看到的第一条 WM_INPUT。</summary>
    public bool RecordThreadWmInput(bool targetsRegisteredWindow)
    {
        if (!targetsRegisteredWindow)
        {
            _threadWmInputOtherHwnd++;
        }

        return ++_threadWmInput == 1;
    }

    public long ThreadWmInput => _threadWmInput;

    public long HookMessages => _hookMessages;

    public void RecordKeyDown() => _keyDowns++;

    public void RecordReadFailure() => _readFailures++;

    public void RecordEmptyDevicePath() => _emptyDevicePaths++;

    public void RecordRejectedDevice() => _rejectedDevices++;

    public void RecordDispatched() => _dispatched++;

    /// <summary>返回 true 表示注册健康状态发生变化（含首次核对），需要记日志。</summary>
    public bool RecordRegistrationState(bool healthy)
    {
        if (_registrationHealthy == healthy)
        {
            return false;
        }

        _registrationHealthy = healthy;
        return true;
    }

    /// <summary>首次一定上报；之后计数有变化且距上次至少 interval 才上报，避免刷屏。</summary>
    public bool ShouldReportSummary(DateTimeOffset now, TimeSpan interval)
    {
        var summary = Describe();
        var isFirst = _lastSummary is null;
        if (!isFirst && (string.Equals(summary, _lastSummary, StringComparison.Ordinal) || now - _lastSummaryAt < interval))
        {
            return false;
        }

        _lastSummary = summary;
        _lastSummaryAt = now;
        return true;
    }

    public bool ShouldReportNonRawDelivery(DateTimeOffset now, TimeSpan interval)
    {
        // 现场排查时前几次扫码都要能对照，之后再按间隔限流。
        if (++_nonRawReports <= 5)
        {
            _lastNonRawReportAt = now;
            return true;
        }

        if (now - _lastNonRawReportAt < interval)
        {
            return false;
        }

        _lastNonRawReportAt = now;
        return true;
    }

    public string Describe() =>
        $"wmInput={_windowMessages} threadWmInput={_threadWmInput} threadWmInputOtherHwnd={_threadWmInputOtherHwnd} hookMessages={_hookMessages} keyDown={_keyDowns} readFailures={_readFailures} emptyDevicePath={_emptyDevicePaths} " +
        $"rejectedDevice={_rejectedDevices} dispatched={_dispatched} registrationHealthy={_registrationHealthy?.ToString() ?? "unknown"}";
}
