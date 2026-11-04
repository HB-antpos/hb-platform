using System.Diagnostics;
using Zeller.PaymentsSdk;
using Zeller.PaymentsSdk.Enums;
using Zeller.PaymentsSdk.Models;
using Zeller.PaymentsSdk.Responses;
using Zeller.PaymentsSdk.Utils;

namespace ZellerProbe;

/// <summary>
/// 第 0 阶段验证窗体：逐项触发 SDK 调用，把每次调用前后的状态写入本地记录，供事后分析。
/// </summary>
internal sealed class ProbeForm : Form
{
    private const string VendorName = "HotBargain";
    private const string VendorApplicationName = "HBPOS-ZellerProbe";
    private const string VendorApplicationVersion = "0.1.0";
    private const string VendorDeviceType = "POS";

    private readonly ProbeJournal _journal;
    private readonly Terminal _terminal;
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };

    private readonly CheckBox _unattended = new() { Text = "无人值守 unattended", AutoSize = true };
    private readonly CheckBox _terminalReceiptPrint = new() { Text = "刷卡机显示打印小票按钮", AutoSize = true };
    private readonly CheckBox _terminalReceipt = new() { Text = "刷卡机显示电子小票按钮", AutoSize = true };
    private readonly CheckBox _fullscreen = new() { Text = "全屏", AutoSize = true };

    private readonly NumericUpDown _purchaseCents = new() { Minimum = 1, Maximum = 1_000_000, Value = 100, Width = 90 };
    private readonly TextBox _purchaseReference = new() { Width = 330, ReadOnly = true };
    private readonly CheckBox _reuseReference = new() { Text = "复用上面的 reference（重复提交测试）", AutoSize = true };

    private readonly TextBox _queryReference = new() { Width = 330, PlaceholderText = "留空 = 最近一笔" };
    private readonly NumericUpDown _pollIntervalSeconds = new() { Minimum = 1, Maximum = 300, Value = 5, Width = 60 };
    private readonly NumericUpDown _pollMinutes = new() { Minimum = 1, Maximum = 120, Value = 10, Width = 60 };

    private readonly NumericUpDown _refundCents = new() { Minimum = 1, Maximum = 1_000_000, Value = 100, Width = 90 };
    private readonly TextBox _refundPurchase = new() { Width = 330, PlaceholderText = "原交易 reference 或 transactionUuid" };
    private readonly CheckBox _cardPresent = new() { Text = "持卡退款 cardPresent（不引用原交易）", AutoSize = true };

    private readonly List<Button> _actionButtons = [];
    private Action? _cancelCurrent;
    private CancellationTokenSource? _pollCancellation;
    private int _inFlight;

    public ProbeForm()
    {
        Text = $"Zeller 验证程序（SDK {typeof(Terminal).Assembly.GetName().Version}）";
        Width = 1180;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;

        var recordDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HbZellerProbe");
        _journal = new ProbeJournal(recordDirectory);
        _terminal = new Terminal(VendorApplicationName, VendorApplicationVersion, VendorDeviceType, VendorName);
        _purchaseReference.Text = NewPurchaseReference();

        BuildLayout(recordDirectory);
        FormClosed += (_, _) =>
        {
            _pollCancellation?.Cancel();
            // 中文注释：同步释放，保证进程退出前 SDK 的 WebView 已关闭。
            _terminal.Dispose();
        };

        // 中文注释：记录运行环境。设备注册由 SDK 用 Windows DPAPI 加密保存，换 Windows 用户可能失效，所以要记下用户名。
        Log($"启动 机器={Environment.MachineName} Windows 用户={Environment.UserName} SDK={typeof(Terminal).Assembly.GetName().Version} 记录目录={recordDirectory}");
        var unfinished = _journal.FindUnfinished();
        if (unfinished.Count > 0)
        {
            Log($"发现 {unfinished.Count} 条上次没有结果的调用（进程中途退出）。点「列出未完成调用」查看，点「逐条查询未完成」核对结果。");
        }
    }

    private static string NewPurchaseReference() => $"HBZP-{Guid.NewGuid():N}";

    private static string NewRefundReference() => $"HBZPR-{Guid.NewGuid():N}";

    private void BuildLayout(string recordDirectory)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(8)
        };

        panel.Controls.Add(Group(
            "1. 配对与设置",
            ActionButton("初始化 initialise", InitialiseAsync),
            ActionButton("配对 / 设置 setup", SetupAsync),
            _unattended,
            _terminalReceipt,
            _terminalReceiptPrint,
            _fullscreen,
            ActionButton("应用设置 configure", ConfigureAsync)));

        panel.Controls.Add(Group(
            "2. 扣款",
            Label("金额（分）"),
            _purchaseCents,
            Label("reference"),
            _purchaseReference,
            _reuseReference,
            ActionButton("扣款 purchase", PurchaseAsync),
            PlainButton("取消当前操作 cancel", CancelCurrent),
            PlainButton("忙碌时再查一次（Operation Busy 测试）", () => _ = QueryAsync(_purchaseReference.Text))));

        panel.Controls.Add(Group(
            "3. 查询与轮询",
            Label("reference"),
            _queryReference,
            ActionButton("查询 getTransactions", () => QueryAsync(_queryReference.Text)),
            Label("每"),
            _pollIntervalSeconds,
            Label("秒，最多"),
            _pollMinutes,
            Label("分钟"),
            PlainButton("开始轮询", () => _ = PollAsync(_queryReference.Text)),
            PlainButton("停止轮询", () => _pollCancellation?.Cancel())));

        panel.Controls.Add(Group(
            "4. 退款",
            Label("金额（分）"),
            _refundCents,
            Label("原交易"),
            _refundPurchase,
            _cardPresent,
            ActionButton("退款 refund", RefundAsync)));

        panel.Controls.Add(Group(
            "5. 未完成记录与文件",
            PlainButton("列出未完成调用", ListUnfinished),
            ActionButton("逐条查询未完成", QueryUnfinishedAsync),
            PlainButton("打开记录目录", () => Process.Start(new ProcessStartInfo(recordDirectory) { UseShellExecute = true }))));

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 400 };
        split.Panel1.Controls.Add(panel);
        split.Panel1.AutoScroll = true;
        split.Panel2.Controls.Add(_log);
        Controls.Add(split);
    }

    private static GroupBox Group(string title, params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, MaximumSize = new Size(1120, 0) };
        flow.Controls.AddRange(controls);
        var group = new GroupBox { Text = title, AutoSize = true, MinimumSize = new Size(1120, 0), Padding = new Padding(6) };
        group.Controls.Add(flow);
        return group;
    }

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 7, 0, 0) };

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) => await action();
        _actionButtons.Add(button);
        return button;
    }

    private static Button PlainButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => action();
        return button;
    }

    private async Task InitialiseAsync() =>
        await InvokeAsync("initialise", null, null, null, () => _terminal.initialise());

    private async Task SetupAsync() =>
        await InvokeAsync("setup", null, null, null, () => _terminal.setup(fullscreen: _fullscreen.Checked));

    private Task ConfigureAsync()
    {
        // 中文注释：configure 是同步方法，不弹界面，只保存之后每次调用的默认参数。
        var response = _terminal.configure(
            fullscreen: _fullscreen.Checked,
            terminalReceipt: _terminalReceipt.Checked,
            terminalReceiptPrint: _terminalReceiptPrint.Checked,
            unattended: _unattended.Checked);
        Log($"configure → {Summarize(response)}");
        _journal.WriteLog(new { At = DateTimeOffset.Now, Operation = "configure", Result = ProbeJournal.Serialize(response) });
        return Task.CompletedTask;
    }

    private async Task PurchaseAsync()
    {
        if (!_reuseReference.Checked)
        {
            _purchaseReference.Text = NewPurchaseReference();
        }

        var reference = _purchaseReference.Text;
        var amount = (int)_purchaseCents.Value;
        _queryReference.Text = reference;
        _refundPurchase.Text = reference;
        _refundCents.Value = amount;
        await InvokeAsync(
            "purchase",
            reference,
            amount,
            null,
            () => _terminal.purchase(amount, reference, unattended: _unattended.Checked ? true : null));
    }

    private async Task RefundAsync()
    {
        var reference = NewRefundReference();
        var amount = (int)_refundCents.Value;
        var purchase = string.IsNullOrWhiteSpace(_refundPurchase.Text) ? null : _refundPurchase.Text.Trim();
        await InvokeAsync(
            "refund",
            reference,
            amount,
            purchase,
            () => _terminal.refund(
                amount,
                purchase: _cardPresent.Checked ? null : purchase,
                cardPresent: _cardPresent.Checked ? true : null,
                reference: reference));
    }

    private Task QueryAsync(string? reference)
    {
        // 中文注释：文档约定空 reference 返回最近一笔；传空字符串而不是 null，避免被 SDK 的参数校验拒绝。
        var normalized = string.IsNullOrWhiteSpace(reference) ? string.Empty : reference.Trim();
        return InvokeAsync("getTransactions", normalized.Length == 0 ? null : normalized, null, null, () => _terminal.getTransactions(normalized));
    }

    /// <summary>
    /// 断网、杀进程之后用来测「多久能按 reference 查到交易」：按间隔反复查询，直到查到或超时。
    /// </summary>
    private async Task PollAsync(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            Log("轮询需要填写 reference。");
            return;
        }

        _pollCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes((double)_pollMinutes.Value));
        _pollCancellation = cancellation;
        var normalized = reference.Trim();
        var startedAt = DateTimeOffset.Now;
        var interval = TimeSpan.FromSeconds((double)_pollIntervalSeconds.Value);
        Log($"开始轮询 reference={normalized} 间隔={interval.TotalSeconds}s");
        var round = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                round++;
                var response = await InvokeAsync("getTransactions(poll)", normalized, null, null, () => _terminal.getTransactions(normalized), quiet: true);
                if (response is GetTransactionsSuccessResponse { Result.Length: > 0 } found)
                {
                    var elapsed = DateTimeOffset.Now - startedAt;
                    Log($"轮询第 {round} 次查到 {found.Result.Length} 笔，距开始轮询 {elapsed.TotalSeconds:0.0} 秒：{Summarize(found)}");
                    _journal.WriteLog(new { At = DateTimeOffset.Now, Operation = "poll-found", Reference = normalized, Round = round, ElapsedSeconds = elapsed.TotalSeconds });
                    return;
                }

                Log($"轮询第 {round} 次：{Summarize(response)}");
                await Task.Delay(interval, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }

        Log($"轮询结束，{round} 次都没有查到 reference={normalized}");
        _journal.WriteLog(new { At = DateTimeOffset.Now, Operation = "poll-not-found", Reference = normalized, Round = round });
    }

    private void ListUnfinished()
    {
        var unfinished = _journal.FindUnfinished();
        Log(unfinished.Count == 0 ? "没有未完成的调用。" : $"未完成的调用 {unfinished.Count} 条：");
        foreach (var entry in unfinished)
        {
            Log($"  {entry.At:yyyy-MM-dd HH:mm:ss} {entry.Operation} reference={entry.Reference ?? "-"} amount={entry.AmountCents?.ToString() ?? "-"} callId={entry.CallId}");
        }
    }

    private async Task QueryUnfinishedAsync()
    {
        foreach (var entry in _journal.FindUnfinished().Where(entry => entry.Reference is not null))
        {
            Log($"核对未完成调用 {entry.Operation} reference={entry.Reference}");
            await QueryAsync(entry.Reference);
        }
    }

    private void CancelCurrent()
    {
        var cancel = _cancelCurrent;
        if (cancel is null)
        {
            Log("当前没有进行中的操作。");
            return;
        }

        Log("调用 cancel()");
        _journal.WriteLog(new { At = DateTimeOffset.Now, Operation = "cancel" });
        try
        {
            cancel();
        }
        catch (Exception ex)
        {
            Log($"cancel() 抛出 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 统一的调用入口：先写「调用前」记录，再调用 SDK，最后写「结果」记录。
    /// </summary>
    private async Task<TResponse?> InvokeAsync<TResponse>(
        string operation,
        string? reference,
        int? amountCents,
        string? originalPurchase,
        Func<OperationHandle<TResponse>> start,
        bool quiet = false)
    {
        var callId = Guid.NewGuid().ToString("N");
        _journal.WriteJournal(new JournalEntry(callId, "before-call", operation, reference, amountCents, originalPurchase, DateTimeOffset.Now));
        if (!quiet)
        {
            Log($"→ {operation} reference={reference ?? "-"} amount={amountCents?.ToString() ?? "-"} original={originalPurchase ?? "-"}");
        }

        SetBusy(+1);
        var stopwatch = Stopwatch.StartNew();
        Action? ownCancel = null;
        try
        {
            var handle = start();
            // 中文注释：只有会弹出界面的操作才登记「取消当前操作」，查询类调用不能覆盖正在等刷卡的那笔。
            if (operation is "purchase" or "refund" or "setup")
            {
                ownCancel = handle.cancel;
                _cancelCurrent = ownCancel;
            }

            handle.onEvent(evt => Log($"   事件 {operation}: {evt.Event} {evt.Message}"));
            var response = await handle;
            stopwatch.Stop();
            WriteResult(callId, operation, reference, amountCents, originalPurchase, response?.GetType().Name ?? "null", ProbeJournal.Serialize(response), stopwatch);
            if (!quiet)
            {
                Log($"← {operation} {stopwatch.ElapsedMilliseconds}ms {Summarize(response)}");
            }

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteResult(callId, operation, reference, amountCents, originalPurchase, $"exception:{ex.GetType().Name}", ProbeJournal.Serialize(new { ex.Message, Stack = ex.StackTrace }), stopwatch);
            Log($"← {operation} {stopwatch.ElapsedMilliseconds}ms 抛出 {ex.GetType().Name}: {ex.Message}");
            return default;
        }
        finally
        {
            if (ownCancel is not null && ReferenceEquals(_cancelCurrent, ownCancel))
            {
                _cancelCurrent = null;
            }

            SetBusy(-1);
        }
    }

    private void WriteResult(string callId, string operation, string? reference, int? amountCents, string? originalPurchase, string resultType, string resultJson, Stopwatch stopwatch) =>
        _journal.WriteJournal(new JournalEntry(callId, "result", operation, reference, amountCents, originalPurchase, DateTimeOffset.Now, resultType, resultJson, stopwatch.ElapsedMilliseconds));

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
        ConfigureSuccessResponse success => $"设置已保存 result={success.Result}",
        PurchaseErrorResponse error => Error(error.Result),
        RefundErrorResponse error => Error(error.Result),
        GetTransactionsErrorResponse error => Error(error.Result),
        InitialiseErrorResponse error => Error(error.Result),
        SetupErrorResponse error => Error(error.Result),
        ConfigureErrorResponse error => Error(error.Result),
        _ => $"{response.GetType().Name} {ProbeJournal.Serialize(response)}"
    };

    private static string Error(ErrorResponseData? error) =>
        error is null ? "错误（无详情）" : $"错误 type={error.Type}({(int)error.Type}) message={error.Message}";

    private static string Transaction(TransactionResponseData? t) =>
        t is null
            ? "（空交易）"
            : $"status={t.Status} type={t.Type} code={t.ResponseCode} text={t.ResponseText} amount={t.Amount} uuid={t.TransactionUuid} external={t.ExternalReference} rrn={t.Rrn} approval={t.ApprovalCode} pan={t.PanMasked} scheme={t.Scheme} time={t.TimestampLocal} receiptText={(string.IsNullOrEmpty(t.ReceiptText) ? "无" : $"{t.ReceiptText.Length} 字")} receiptLink={(string.IsNullOrEmpty(t.ReceiptLink) ? "无" : "有")}";

    private void SetBusy(int delta)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetBusy(delta));
            return;
        }

        _inFlight += delta;
        // 中文注释：进行中只锁住会发起新交易的按钮；取消、并发测试、轮询按钮保持可用。
        foreach (var button in _actionButtons)
        {
            button.Enabled = _inFlight == 0;
        }
    }

    private void Log(string message)
    {
        _journal.WriteLog(new { At = DateTimeOffset.Now, Message = message });
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        AppendLog(message);
    }

    private void AppendLog(string message) =>
        _log.AppendText($"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
}
