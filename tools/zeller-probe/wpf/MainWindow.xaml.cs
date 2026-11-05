using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Zeller.PaymentsSdk;
using Zeller.PaymentsSdk.Enums;
using Zeller.PaymentsSdk.Models;
using Zeller.PaymentsSdk.Responses;
using Zeller.PaymentsSdk.Utils;
using ZellerProbe;

namespace ZellerWpfSpike;

/// <summary>
/// 第 2 阶段第一步的技术验证窗体：在 WPF 进程里调用 Zeller SDK，回答第 0 阶段（WinForms）没覆盖的问题。
/// </summary>
/// <remarks>
/// 中文注释：和第 0 阶段验证程序的主要区别——
/// 1. 宿主是 WPF，窗口可以切到和收银端一样的「最大化、无边框」，并能模拟扫码时的抢焦点；
/// 2. 所有按钮始终可点，用来验证上一笔扣款挂起时还能不能发起下一笔；
/// 3. 扣款带看门狗（方案 D11）：超时后调用 cancel() 并改为按 reference 轮询；
/// 4. 可以作废并重建 Terminal，看重建后是否还保持配对。
/// </remarks>
public partial class MainWindow : Window
{
    private const string SdkStorageDirectory = @"C:\ProgramData\zeller-sdk";

    private readonly ProbeJournal _journal;
    private readonly string _recordDirectory;
    private readonly DispatcherTimer _scannerTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<IntPtr, string> _otherWindows = [];
    private Terminal _terminal;
    private int _terminalNumber = 1;
    private IntPtr _mainHandle;
    private string _foreground = string.Empty;
    private bool _posMode;
    private Action? _cancelLatest;
    private CancellationTokenSource? _pollCancellation;

    // 中文注释：SDK 同一时刻只能有一个调用。实测在上一笔没返回时再发起调用（即使只是查询），
    // 新调用返回 OperationBusy，而上一笔的结果会丢失、之后的调用还会串线。
    // 所以默认「严格串行」：记下进行中的调用，期间拒绝发起新的调用。全部在界面线程读写，不需要锁。
    private string? _inFlightCallId;
    private string? _inFlightDescription;

    public MainWindow()
    {
        InitializeComponent();

        _recordDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HbZellerWpfSpike");
        _journal = new ProbeJournal(_recordDirectory);

        // 中文注释：SDK 文档要求在构造 Terminal 之前设置日志工厂。
        Terminal.LoggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new FileLoggerProvider(System.IO.Path.Combine(_recordDirectory, "zeller-sdk.log"), Log)));
        _terminal = CreateTerminal();

        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            Log($"未处理异常（界面线程）{e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"未观察的任务异常 {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.SetObserved();
        };

        SourceInitialized += (_, _) => _mainHandle = new WindowInteropHelper(this).Handle;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _scannerTimer.Tick += (_, _) => ActivateForScannerInput();
        _windowTimer.Tick += (_, _) => WatchWindows();
        _windowTimer.Start();

        var args = Environment.GetCommandLineArgs();
        ApplyWindowMode(posMode: !args.Contains("--windowed", StringComparer.OrdinalIgnoreCase));
    }

    private Terminal CreateTerminal() =>
        new(VendorAppBox.Text.Trim(), VendorVersionBox.Text.Trim(), VendorDeviceBox.Text.Trim(), VendorNameBox.Text.Trim());

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log($"启动 机器={Environment.MachineName} Windows 用户={Environment.UserName} SDK={typeof(Terminal).Assembly.GetName().Version} 宿主=WPF");
        Log($"程序路径={Environment.ProcessPath}");
        Log($"记录目录={_recordDirectory}");
        Log($"Logging.Abstractions 运行版本={typeof(ILogger).Assembly.GetName().Version}（SDK 按 6.0.4 编译）");
        LogSdkStorage("启动时");

        var unfinished = _journal.FindUnfinished();
        if (unfinished.Count > 0)
        {
            Log($"发现 {unfinished.Count} 条上次没有结果的调用：");
            foreach (var entry in unfinished)
            {
                Log($"  {entry.At:MM-dd HH:mm:ss} {entry.Operation} reference={entry.Reference ?? "-"}");
            }
        }

        if (Environment.GetCommandLineArgs().Contains("--auto-init", StringComparer.OrdinalIgnoreCase))
        {
            Log("命令行带 --auto-init：自动执行一次初始化。");
            await InitialiseAsync();
        }

        // 中文注释：--query <reference> 可重复，启动后依次按 reference 查询，用来核实上一次运行留下的交易。
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--query", StringComparison.OrdinalIgnoreCase))
            {
                ReferenceBox.Text = args[i + 1];
                await QueryAsync(args[i + 1]);
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _pollCancellation?.Cancel();
        _scannerTimer.Stop();
        _windowTimer.Stop();
        try
        {
            _terminal.Dispose();
        }
        catch (Exception ex)
        {
            _journal.WriteLog(new { At = DateTimeOffset.Now, Message = $"退出时释放 Terminal 抛出 {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ───────────── 0. 窗口环境 ─────────────

    private void OnToggleWindowMode(object sender, RoutedEventArgs e) => ApplyWindowMode(!_posMode);

    private void ApplyWindowMode(bool posMode)
    {
        // 中文注释：收银端主窗口是 WindowStyle=None、ResizeMode=CanResize、WindowState=Maximized。
        WindowState = WindowState.Normal;
        WindowStyle = posMode ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = posMode ? WindowState.Maximized : WindowState.Normal;
        _posMode = posMode;
        ModeText.Text = posMode ? "当前：收银端模式（最大化、无边框）" : "当前：普通窗口";
        if (IsLoaded)
        {
            Log($"窗口模式 → {ModeText.Text}");
        }
    }

    private void OnHostTopmostChanged(object sender, RoutedEventArgs e)
    {
        Topmost = HostTopmostBox.IsChecked == true;
        Log($"主窗口置顶 = {Topmost}");
    }

    private void OnScannerChanged(object sender, RoutedEventArgs e)
    {
        if (ScannerBox.IsChecked == true)
        {
            _scannerTimer.Start();
            Log("开始模拟扫码抢焦点：每 2 秒执行一次和收银端 ActivateForScannerInput 相同的动作。");
        }
        else
        {
            _scannerTimer.Stop();
            Log("停止模拟扫码抢焦点。");
        }
    }

    /// <summary>与收银端 MainWindow.ActivateForScannerInput 相同：临时置顶、激活、取焦点，再恢复。</summary>
    private void ActivateForScannerInput()
    {
        var wasTopmost = Topmost;
        Topmost = true;
        Activate();
        Focus();
        Topmost = wasTopmost;
    }

    private void WatchWindows()
    {
        if (WatchWindowsBox.IsChecked != true || _mainHandle == IntPtr.Zero)
        {
            return;
        }

        var current = NativeWindows.ListVisibleTopLevel(Environment.ProcessId)
            .Where(window => window.Handle != _mainHandle)
            .ToDictionary(window => window.Handle, window => window.Describe(_mainHandle));

        foreach (var (handle, description) in current)
        {
            if (!_otherWindows.TryGetValue(handle, out var previous))
            {
                Log($"[窗口] 出现 {description}");
            }
            else if (!string.Equals(previous, description, StringComparison.Ordinal))
            {
                Log($"[窗口] 变化 {description}");
            }
        }

        foreach (var handle in _otherWindows.Keys.Where(handle => !current.ContainsKey(handle)).ToList())
        {
            Log($"[窗口] 消失 0x{handle.ToInt64():X}");
        }

        _otherWindows.Clear();
        foreach (var (handle, description) in current)
        {
            _otherWindows[handle] = description;
        }

        var foregroundHandle = NativeWindows.GetForegroundWindow();
        var foreground = foregroundHandle == _mainHandle
            ? "主窗口"
            : current.ContainsKey(foregroundHandle) ? $"SDK 窗口 0x{foregroundHandle.ToInt64():X}" : "其他程序";
        if (current.Count > 0 && !string.Equals(foreground, _foreground, StringComparison.Ordinal))
        {
            Log($"[窗口] 前台 → {foreground}");
        }

        _foreground = foreground;
    }

    private void OnSetOwner(object sender, RoutedEventArgs e)
    {
        if (_otherWindows.Count == 0)
        {
            Log("当前没有 SDK 窗口。先发起扣款或配对，等窗口出现后再点。");
            return;
        }

        foreach (var handle in _otherWindows.Keys.ToList())
        {
            var ok = NativeWindows.TrySetOwner(handle, _mainHandle, out var error);
            Log($"[窗口] 设置 Owner 0x{handle.ToInt64():X} → 主窗口：{(ok ? "成功" : $"失败，错误码 {error}")}");
        }
    }

    private void OnEnableWinFormsInterop(object sender, RoutedEventArgs e)
    {
        // 中文注释：WPF 进程里的非模态 WinForms 窗口可能收不到键盘输入，这是微软给的互操作开关。
        // 只在「登录页打不了字」时才需要点，用反射调用是为了不让本工程直接依赖 WinForms。
        try
        {
            var type = Type.GetType("System.Windows.Forms.Integration.WindowsFormsHost, WindowsFormsIntegration", throwOnError: true)!;
            type.GetMethod("EnableWindowsFormsInterop")!.Invoke(null, null);
            Log("已调用 WindowsFormsHost.EnableWindowsFormsInterop()。");
        }
        catch (Exception ex)
        {
            Log($"启用 WinForms 键盘互操作失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_recordDirectory) { UseShellExecute = true });

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    // ───────────── 1. Terminal 实例与配对 ─────────────

    private async void OnInitialise(object sender, RoutedEventArgs e) => await InitialiseAsync();

    private async Task InitialiseAsync()
    {
        var terminal = _terminal;
        await InvokeAsync("initialise", null, null, null, () => terminal.initialise());
        LogSdkStorage("初始化后");
    }

    private async void OnSetup(object sender, RoutedEventArgs e)
    {
        var terminal = _terminal;
        var fullscreen = FullscreenBox.IsChecked == true;
        await InvokeAsync("setup", null, null, null, () => terminal.setup(fullscreen: fullscreen));
        LogSdkStorage("配对后");
    }

    private void OnRebuild(object sender, RoutedEventArgs e) => RebuildTerminal("手动");

    private void RebuildTerminal(string reason)
    {
        var old = _terminal;
        var oldNumber = _terminalNumber;
        Log($"开始释放 Terminal#{oldNumber}（{reason}；如果界面在这一行之后卡住，说明 Dispose 被挂起的调用拖住了）");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            old.Dispose();
            Log($"Terminal#{oldNumber} 已释放，用时 {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log($"释放 Terminal#{oldNumber} 抛出 {ex.GetType().Name}: {ex.Message}");
        }

        // 中文注释：旧实例上没返回的调用随实例一起作废，放开严格串行。
        if (_inFlightCallId is not null)
        {
            Log($"旧实例上未返回的调用（{_inFlightDescription}）已随实例作废。");
            _inFlightCallId = null;
            _inFlightDescription = null;
        }

        _cancelLatest = null;
        _terminal = CreateTerminal();
        _terminalNumber++;
        Log($"已新建 Terminal#{_terminalNumber}（应用名={VendorAppBox.Text.Trim()} 版本={VendorVersionBox.Text.Trim()} 设备类型={VendorDeviceBox.Text.Trim()} 厂商={VendorNameBox.Text.Trim()}）。请点「初始化」看是否仍已配对。");
    }

    // ───────────── 2. 扣款 ─────────────

    private async void OnPurchase(object sender, RoutedEventArgs e)
    {
        if (!TryReadAmount(out var amount))
        {
            return;
        }

        var reference = $"HBZW-{Guid.NewGuid():N}";
        ReferenceBox.Text = reference;
        bool? fullscreen = FullscreenBox.IsChecked == true ? true : null;
        bool? unattended = UnattendedBox.IsChecked == true ? true : null;
        bool? terminalReceipt = TerminalReceiptBox.IsChecked == true ? true : null;
        bool? terminalReceiptPrint = TerminalReceiptPrintBox.IsChecked == true ? true : null;
        Watchdog? watchdog = null;
        if (WatchdogBox.IsChecked == true
            && int.TryParse(WatchdogTotalBox.Text, out var totalSeconds)
            && int.TryParse(WatchdogProcessingBox.Text, out var processingSeconds))
        {
            watchdog = new Watchdog(TimeSpan.FromSeconds(totalSeconds), TimeSpan.FromSeconds(processingSeconds));
        }

        var terminal = _terminal;
        await InvokeAsync(
            "purchase",
            reference,
            amount,
            null,
            () => terminal.purchase(
                amount,
                reference,
                fullscreen: fullscreen,
                terminalReceipt: terminalReceipt,
                terminalReceiptPrint: terminalReceiptPrint,
                unattended: unattended),
            watchdog: watchdog,
            note: $"unattended={unattended == true} fullscreen={fullscreen == true}");
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        var cancel = _cancelLatest;
        if (cancel is null)
        {
            Log("还没有可以取消的操作。");
            return;
        }

        Log("调用 cancel()");
        try
        {
            cancel();
        }
        catch (Exception ex)
        {
            Log($"cancel() 抛出 {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ───────────── 3. 查询与退款 ─────────────

    private async void OnQuery(object sender, RoutedEventArgs e) => await QueryAsync(ReferenceBox.Text);

    private Task QueryAsync(string? reference)
    {
        var normalized = string.IsNullOrWhiteSpace(reference) ? string.Empty : reference.Trim();
        var terminal = _terminal;
        return InvokeAsync("getTransactions", normalized.Length == 0 ? null : normalized, null, null, () => terminal.getTransactions(normalized));
    }

    private void OnPoll(object sender, RoutedEventArgs e) => _ = PollAsync(ReferenceBox.Text);

    private void OnStopPoll(object sender, RoutedEventArgs e) => _pollCancellation?.Cancel();

    /// <summary>方案 D3 的自动重查：每 5 秒按 reference 查一次，最多 3 分钟，查到就停。</summary>
    private async Task PollAsync(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            Log("轮询需要填写 reference。");
            return;
        }

        _pollCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _pollCancellation = cancellation;
        var normalized = reference.Trim();
        var startedAt = DateTimeOffset.Now;
        Log($"开始轮询 reference={normalized} 间隔=5s 最多 3 分钟");
        var round = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                round++;
                if (StrictBox.IsChecked == true && _inFlightCallId is not null)
                {
                    Log($"轮询第 {round} 次：跳过（{_inFlightDescription} 还没返回，严格串行）");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                    continue;
                }

                var terminal = _terminal;
                var response = await InvokeAsync("getTransactions(poll)", normalized, null, null, () => terminal.getTransactions(normalized), quiet: true);
                if (response is GetTransactionsSuccessResponse { Result.Length: > 0 } found)
                {
                    var elapsed = DateTimeOffset.Now - startedAt;
                    Log($"轮询第 {round} 次查到 {found.Result.Length} 笔，距开始轮询 {elapsed.TotalSeconds:0.0} 秒：{Summarize(found)}");
                    _journal.WriteLog(new { At = DateTimeOffset.Now, Operation = "poll-found", Reference = normalized, Round = round, ElapsedSeconds = elapsed.TotalSeconds });
                    return;
                }

                Log($"轮询第 {round} 次：{Summarize(response)}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }

        Log($"轮询结束，{round} 次都没有查到 reference={normalized}");
        _journal.WriteLog(new { At = DateTimeOffset.Now, Operation = "poll-not-found", Reference = normalized, Round = round });
    }

    private async void OnRefund(object sender, RoutedEventArgs e)
    {
        var original = ReferenceBox.Text.Trim();
        if (original.Length == 0)
        {
            Log("退款需要在上面填原交易的 reference 或 transactionUuid。");
            return;
        }

        if (!TryReadAmount(out var amount))
        {
            return;
        }

        var reference = $"HBZWR-{Guid.NewGuid():N}";
        var terminal = _terminal;
        await InvokeAsync("refund", reference, amount, original, () => terminal.refund(amount, purchase: original, reference: reference));
    }

    private bool TryReadAmount(out int amount)
    {
        if (int.TryParse(AmountBox.Text, out amount) && amount > 0)
        {
            return true;
        }

        Log("金额（分）必须是正整数。");
        return false;
    }

    // ───────────── 调用入口 ─────────────

    private sealed record Watchdog(TimeSpan Total, TimeSpan AfterProcessing);

    /// <summary>
    /// 统一的调用入口：先写「调用前」记录，再调用 SDK，最后写「结果」记录。带看门狗时，超时后不再等 SDK。
    /// </summary>
    private async Task<TResponse?> InvokeAsync<TResponse>(
        string operation,
        string? reference,
        int? amountCents,
        string? originalPurchase,
        Func<OperationHandle<TResponse>> start,
        bool quiet = false,
        Watchdog? watchdog = null,
        string? note = null)
    {
        var callId = Guid.NewGuid().ToString("N");
        var terminalNumber = _terminalNumber;
        var strict = StrictBox.IsChecked == true;
        if (strict && _inFlightCallId is not null)
        {
            Log($"× {operation} 没有发出：{_inFlightDescription} 还没返回（严格串行）。");
            return default;
        }

        if (strict)
        {
            _inFlightCallId = callId;
            _inFlightDescription = $"{operation}{(reference is null ? string.Empty : " " + reference)}";
        }

        var keepInFlight = false;
        var watchdogFired = false;
        Action? ownCancel = null;
        _journal.WriteJournal(new JournalEntry(callId, "before-call", operation, reference, amountCents, originalPurchase, DateTimeOffset.Now));
        if (!quiet)
        {
            Log($"→ {operation} [Terminal#{terminalNumber}] reference={reference ?? "-"} amount={amountCents?.ToString() ?? "-"} original={originalPurchase ?? "-"}{(note is null ? string.Empty : " " + note)}");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // 中文注释：正式实现会把调用封送到界面线程；勾选后改在线程池发起，用来确认 SDK 对线程的要求。
            var handle = BackgroundThreadBox.IsChecked == true ? await Task.Run(start) : start();
            Action cancel = handle.cancel;
            if (operation is "purchase" or "refund" or "setup")
            {
                // 中文注释：实测 cancel() 不认调用——在旧调用的句柄上调 cancel()，取消的是当时正在进行的那一个。
                // 所以只在本调用进行期间登记，结束后立即清掉。
                ownCancel = cancel;
                _cancelLatest = cancel;
            }

            var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handle.onEvent(evt =>
            {
                Log($"   事件 {operation}: {evt.Event} {evt.Message}");
                if (string.Equals(evt.Event.ToString(), "Processing", StringComparison.Ordinal))
                {
                    processing.TrySetResult();
                }
            });

            var resultTask = AwaitHandleAsync(handle);
            if (watchdog is not null)
            {
                var total = Task.Delay(watchdog.Total);
                var afterProcessing = DelayAfterAsync(processing.Task, watchdog.AfterProcessing);
                var first = await Task.WhenAny(resultTask, total, afterProcessing);
                if (first != resultTask)
                {
                    var why = first == total
                        ? $"总时长超过 {watchdog.Total.TotalSeconds:0} 秒"
                        : $"进入处理后超过 {watchdog.AfterProcessing.TotalSeconds:0} 秒";
                    Log($"[看门狗] 接手 {operation}（{why}，已等 {stopwatch.ElapsedMilliseconds}ms）：调用 cancel()，改为按 reference 轮询");
                    _journal.WriteLog(new { At = DateTimeOffset.Now, Operation = "watchdog-fired", CallId = callId, Reference = reference, Why = why });
                    try
                    {
                        cancel();
                    }
                    catch (Exception ex)
                    {
                        Log($"cancel() 抛出 {ex.GetType().Name}: {ex.Message}");
                    }

                    // 中文注释：正常情况下 cancel() 之后约 1 秒 SDK 就会返回 Cancelled，给它 3 秒。
                    watchdogFired = true;
                    var settled = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(3))) == resultTask;
                    if (!settled)
                    {
                        Log($"[看门狗] cancel() 之后 3 秒仍未返回，{operation} 这次调用作废。");
                        _ = ObserveLateResultAsync(resultTask, callId, operation, reference, amountCents, originalPurchase, stopwatch);
                        if (RebuildOnWatchdogBox.IsChecked == true)
                        {
                            // 重建会清掉「进行中」标记，之后的轮询走新实例。
                            keepInFlight = true;
                            RebuildTerminal("看门狗接手后重建");
                        }
                        else
                        {
                            Log("[看门狗] 没有重建 Terminal（未勾选）。旧调用仍占着 SDK，之后的调用可能串线。");
                        }

                        if (!string.IsNullOrWhiteSpace(reference))
                        {
                            var abandonedReference = reference;
                            _ = Dispatcher.BeginInvoke(() => _ = PollAsync(abandonedReference));
                        }

                        return default;
                    }

                    Log("[看门狗] cancel() 生效，调用已返回；随后按 reference 核实一次。");
                }
            }

            var response = await resultTask;
            stopwatch.Stop();
            WriteResult(callId, operation, reference, amountCents, originalPurchase, response?.GetType().Name ?? "null", ProbeJournal.Serialize(response), stopwatch.ElapsedMilliseconds);
            if (!quiet)
            {
                Log($"← {operation} [Terminal#{terminalNumber}] {stopwatch.ElapsedMilliseconds}ms {Summarize(response)}");
            }

            if (watchdogFired && !string.IsNullOrWhiteSpace(reference))
            {
                var cancelledReference = reference;
                _ = Dispatcher.BeginInvoke(() => _ = PollAsync(cancelledReference));
            }

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteResult(callId, operation, reference, amountCents, originalPurchase, $"exception:{ex.GetType().Name}", ProbeJournal.Serialize(new { ex.Message, Stack = ex.StackTrace }), stopwatch.ElapsedMilliseconds);
            Log($"← {operation} [Terminal#{terminalNumber}] {stopwatch.ElapsedMilliseconds}ms 抛出 {ex.GetType().Name}: {ex.Message}");
            return default;
        }
        finally
        {
            if (ownCancel is not null && ReferenceEquals(_cancelLatest, ownCancel))
            {
                _cancelLatest = null;
            }

            if (!keepInFlight && _inFlightCallId == callId)
            {
                _inFlightCallId = null;
                _inFlightDescription = null;
            }
        }
    }

    private static async Task<TResponse> AwaitHandleAsync<TResponse>(OperationHandle<TResponse> handle) => await handle;

    private static async Task DelayAfterAsync(Task trigger, TimeSpan delay)
    {
        await trigger;
        await Task.Delay(delay);
    }

    /// <summary>看门狗接手之后继续旁观：如果 SDK 后来还是返回了，记下它晚了多久、返回了什么。</summary>
    private async Task ObserveLateResultAsync<TResponse>(
        Task<TResponse> resultTask,
        string callId,
        string operation,
        string? reference,
        int? amountCents,
        string? originalPurchase,
        Stopwatch stopwatch)
    {
        try
        {
            var response = await resultTask;
            WriteResult(callId, operation, reference, amountCents, originalPurchase, "late:" + (response?.GetType().Name ?? "null"), ProbeJournal.Serialize(response), stopwatch.ElapsedMilliseconds);
            Log($"[看门狗] 迟到的返回 {operation} {stopwatch.ElapsedMilliseconds}ms {Summarize(response)}");
        }
        catch (Exception ex)
        {
            WriteResult(callId, operation, reference, amountCents, originalPurchase, $"late-exception:{ex.GetType().Name}", ProbeJournal.Serialize(new { ex.Message }), stopwatch.ElapsedMilliseconds);
            Log($"[看门狗] 迟到的异常 {operation} {stopwatch.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void WriteResult(string callId, string operation, string? reference, int? amountCents, string? originalPurchase, string resultType, string resultJson, long elapsedMs) =>
        _journal.WriteJournal(new JournalEntry(callId, "result", operation, reference, amountCents, originalPurchase, DateTimeOffset.Now, resultType, resultJson, elapsedMs));

    // ───────────── 输出 ─────────────

    /// <summary>记下 SDK 配对文件的大小和修改时间，用来判断某个操作有没有改写配对信息。不读取内容。</summary>
    private void LogSdkStorage(string when)
    {
        try
        {
            var directory = new DirectoryInfo(SdkStorageDirectory);
            if (!directory.Exists)
            {
                Log($"SDK 存储（{when}）：{SdkStorageDirectory} 不存在");
                return;
            }

            var files = directory.GetFiles().Select(file => $"{file.Name} {file.Length} 字节 改于 {file.LastWriteTime:MM-dd HH:mm:ss}");
            Log($"SDK 存储（{when}）：{string.Join("；", files)}");
        }
        catch (Exception ex)
        {
            Log($"读取 SDK 存储目录失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Summarize(object? response) => response switch
    {
        null => "无结果",
        PurchaseSuccessResponse success => "成交 " + Transaction(success.Result),
        RefundSuccessResponse success => "退款成交 " + Transaction(success.Result),
        GetTransactionsSuccessResponse success => success.Result.Length == 0
            ? "查到 0 笔"
            : $"查到 {success.Result.Length} 笔：" + string.Join(" | ", success.Result.Select(Transaction)),
        InitialiseSuccessResponse success => $"初始化成功 result={success.Result}",
        SetupSuccessResponse success => $"配对成功 result={success.Result}",
        PurchaseErrorResponse error => Error(error.Result),
        RefundErrorResponse error => Error(error.Result),
        GetTransactionsErrorResponse error => Error(error.Result),
        InitialiseErrorResponse error => Error(error.Result),
        SetupErrorResponse error => Error(error.Result),
        _ => $"{response.GetType().Name} {ProbeJournal.Serialize(response)}"
    };

    private static string Error(ErrorResponseData? error) =>
        error is null ? "错误（无详情）" : $"错误 type={error.Type}({(int)error.Type}) message={error.Message}";

    private static string Transaction(TransactionResponseData? t) =>
        t is null
            ? "（空交易）"
            : $"status={t.Status} type={t.Type} code={t.ResponseCode} text={t.ResponseText} amount={t.Amount} uuid={t.TransactionUuid} external={t.ExternalReference} rrn={t.Rrn} approval={t.ApprovalCode} pan={t.PanMasked} scheme={t.Scheme} time={t.TimestampLocal} receiptText={(string.IsNullOrEmpty(t.ReceiptText) ? "无" : $"{t.ReceiptText.Length} 字")} receiptLink={(string.IsNullOrEmpty(t.ReceiptLink) ? "无" : "有")}";

    private void Log(string message)
    {
        _journal.WriteLog(new { At = DateTimeOffset.Now, Message = message });
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }

        AppendLog(message);
    }

    private void AppendLog(string message)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
