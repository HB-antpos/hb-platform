using System.Globalization;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>
/// <see cref="IZellerTerminalGateway"/> 的实现：把 Zeller SDK 的缺陷挡在外面，让上层可以放心地使用它。
/// 它不引用 Zeller.PaymentsSdk，SDK 经由 <see cref="IZellerSdkTerminalFactory"/> 接入。
/// </summary>
/// <remarks>
/// 中文注释：这里的每一条规则都来自 2026-11-05 的真机验证（SDK 0.1.0）。
/// 1. 严格串行：同一时刻只有一个调用进入 SDK，查询也排队。SDK 在扣款进行中再收到任何调用时，
///    新调用返回 OperationBusy，进行中的那笔结果丢失、永不返回，之后的调用还会串线（8 次全中）。
/// 2. 取消只对进行中的调用有效：调用方的取消只在那次调用进行期间转成 SDK 的 cancel()，结束后立即解除。
///    在已结束的调用上调 cancel()，被取消的是当时正在进行或下一个调用。
/// 3. 看门狗：扣款超过总时长，或收到「处理中」后超过 45 秒，先 cancel() 再等 3 秒；还不返回就放弃这次调用。
/// 4. 放弃、抛异常、或者 SDK 返回了 OperationBusy / OperationResponseInvalid（说明它的内部状态已经乱了）之后，
///    在放开串行门之前先作废并重建 Terminal。重建只要几毫秒，不需要重新配对，也不需要先初始化。
/// 所以上层看到 Abandoned 时，Terminal 已经是新的了，可以马上按 reference 查询。
/// </remarks>
public sealed class ZellerTerminalGateway : IZellerTerminalGateway, IDisposable
{
    private readonly IZellerSdkTerminalFactory _factory;
    private readonly IZellerUiDispatcher _dispatcher;
    private readonly ZellerGatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private IZellerSdkTerminal? _terminal;
    private TaskCompletionSource? _abandon;
    private int _terminalsCreated;

    // 中文注释：当前占着串行门的操作名，只用来在耗时日志里说明「后来的调用被谁挡住了」，不参与任何判断。
    private string? _activeOperation;

    public ZellerTerminalGateway(
        IZellerSdkTerminalFactory factory,
        IZellerUiDispatcher dispatcher,
        ZellerGatewayOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? new ZellerGatewayOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>到目前为止创建过多少个 SDK Terminal，用于诊断和测试。</summary>
    public int TerminalsCreated => Volatile.Read(ref _terminalsCreated);

    /// <summary>是否有一个调用正占着 SDK。</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    public async Task<ZellerPairingResult> InitialiseAsync(CancellationToken cancellationToken = default)
    {
        var run = await RunAsync(
            "initialise",
            terminal => terminal.Initialise(),
            new CallLimits(_options.InitialiseTimeout, null),
            progress: null,
            new CallEvidence(),
            result => !result.IsPaired && IsBrokenSdkState(result.ErrorType),
            cancellationToken).ConfigureAwait(false);
        return ToPairingResult(run, "initialise");
    }

    public async Task<ZellerPairingResult> SetupAsync(CancellationToken cancellationToken = default)
    {
        // 中文注释：配对要等店长在 Zeller 的页面里登录，所以没有总时长，只响应调用方的取消。
        var run = await RunAsync(
            "setup",
            terminal => terminal.Setup(),
            new CallLimits(null, null),
            progress: null,
            new CallEvidence(),
            result => !result.IsPaired && IsBrokenSdkState(result.ErrorType),
            cancellationToken).ConfigureAwait(false);
        return ToPairingResult(run, "setup");
    }

    public async Task<ZellerCallResult> PurchaseAsync(
        ZellerPurchaseRequest request,
        IProgress<ZellerTerminalEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var evidence = new CallEvidence();
        var run = await RunAsync(
            "purchase",
            terminal => terminal.Purchase(request),
            new CallLimits(_options.TransactionTimeout, _options.AfterProcessingTimeout),
            progress,
            evidence,
            LeavesBrokenSdkState,
            cancellationToken).ConfigureAwait(false);
        return ToCallResult(run, evidence);
    }

    public async Task<ZellerCallResult> RefundAsync(
        ZellerRefundRequest request,
        IProgress<ZellerTerminalEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var evidence = new CallEvidence();
        var run = await RunAsync(
            "refund",
            terminal => terminal.Refund(request),
            new CallLimits(_options.TransactionTimeout, _options.AfterProcessingTimeout),
            progress,
            evidence,
            LeavesBrokenSdkState,
            cancellationToken).ConfigureAwait(false);
        return ToCallResult(run, evidence);
    }

    public async Task<ZellerQueryResult> GetTransactionsAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var run = await RunAsync(
            "query",
            terminal => terminal.GetTransactions(reference),
            new CallLimits(_options.QueryTimeout, null),
            progress: null,
            new CallEvidence(),
            result => !result.Succeeded && IsBrokenSdkState(result.ErrorType),
            cancellationToken).ConfigureAwait(false);

        return run.Kind switch
        {
            RunKind.Completed => run.Value!,
            RunKind.NotStarted => ZellerQueryResult.Failed(ZellerErrorType.Cancelled, "Cancelled before the call reached the Zeller SDK."),
            RunKind.StartFailed => ZellerQueryResult.Failed(ZellerErrorType.OperationFailure, run.Message),
            RunKind.Abandoned => ZellerQueryResult.Failed(ZellerErrorType.OperationFailure, "The Zeller SDK did not answer the query."),
            _ => ZellerQueryResult.Failed(ZellerErrorType.OperationFailure, run.Message)
        };
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        // 中文注释：先让进行中的调用以 Abandoned 结束，再等它放开串行门，然后作废 Terminal。
        SignalAbandon();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecycleAsync("reset requested").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        SignalAbandon();
        var terminal = Interlocked.Exchange(ref _terminal, null);
        if (terminal is null)
        {
            return;
        }

        try
        {
            terminal.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"disposing the terminal at shutdown failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ───────────── 核心：串行执行一次 SDK 调用 ─────────────

    private async Task<GatewayRun<T>> RunAsync<T>(
        string operationName,
        Func<IZellerSdkTerminal, IZellerSdkOperation<T>> start,
        CallLimits limits,
        IProgress<ZellerTerminalEvent>? progress,
        CallEvidence evidence,
        Func<T, bool> leavesBrokenSdkState,
        CancellationToken cancellationToken)
    {
        // 中文注释：耗时计时从调用方发起的这一刻开始。串行门正被别的调用占着时，先记下占着它的是谁
        // （例如取消后 60 秒的迟到复查正在查询），好在日志里解释「为什么这次等了这么久」；读到的值只用于诊断。
        var timings = new CallTimings(_time);
        var blockedBy = _gate.CurrentCount == 0 ? Volatile.Read(ref _activeOperation) : null;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 调用方在排队时就取消了：这段排队时间正是我们想看到的，所以也写一行。
            timings.MarkGateAcquired();
            LogTiming(operationName, nameof(RunKind.NotStarted), timings, blockedBy, TimeSpan.Zero);
            return GatewayRun<T>.NotStarted();
        }

        timings.MarkGateAcquired();
        Volatile.Write(ref _activeOperation, operationName);
        var abandon = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                LogTiming(operationName, nameof(RunKind.NotStarted), timings, blockedBy, TimeSpan.Zero);
                return GatewayRun<T>.NotStarted();
            }

            lock (_stateLock)
            {
                _abandon = abandon;
            }

            var run = await ExecuteAsync(start, limits, progress, evidence, abandon, timings, cancellationToken).ConfigureAwait(false);
            timings.MarkFinished();
            var reason = run.Kind switch
            {
                RunKind.Abandoned => "the call was abandoned",
                RunKind.Faulted => "the call faulted",
                RunKind.Completed when leavesBrokenSdkState(run.Value!) => "the SDK reported a broken internal state",
                _ => null
            };
            // 中文注释：重建 Terminal 发生在放开串行门之前，它的耗时会算进下一次调用的 gateWaitMs，所以单独记一项。
            var recycleElapsed = TimeSpan.Zero;
            if (reason is not null)
            {
                var recycleStartedAt = _time.GetTimestamp();
                await RecycleAsync(reason).ConfigureAwait(false);
                recycleElapsed = _time.GetElapsedTime(recycleStartedAt);
            }

            LogTiming(operationName, run.Kind.ToString(), timings, blockedBy, recycleElapsed);
            return run;
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_abandon, abandon))
                {
                    _abandon = null;
                }
            }

            Volatile.Write(ref _activeOperation, null);
            _gate.Release();
        }
    }

    private async Task<GatewayRun<T>> ExecuteAsync<T>(
        Func<IZellerSdkTerminal, IZellerSdkOperation<T>> start,
        CallLimits limits,
        IProgress<ZellerTerminalEvent>? progress,
        CallEvidence evidence,
        TaskCompletionSource abandon,
        CallTimings timings,
        CancellationToken cancellationToken)
    {
        // 中文注释：所有时限都从事件发生的那一刻算起，而不是从计时器登记的那一刻。
        // 计时器在线程池上登记，线程调度的延迟不能把看门狗的时限拉长。
        var startedAt = _time.GetUtcNow();
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnEvent(ZellerTerminalEvent terminalEvent)
        {
            // 中文注释：事件到达的时刻要最先记下。AwaitingPaymentMethod 就是刷卡机已显示金额、提示刷卡的那一刻，
            // 它和「SDK 调用发出」之间的间隔，就是「点击 Zeller 到刷卡机显示金额」里我们看不到的那一段（云端中转 + 刷卡机）。
            // 事件名是 SDK 的枚举名，不含金额、卡号等内容。
            timings.MarkEvent(terminalEvent.Kind);
            Log($"event {terminalEvent.Name} atMs={timings.SinceRequestMs()}");

            switch (terminalEvent.Kind)
            {
                case ZellerTerminalEventKind.AwaitingPaymentMethod:
                    evidence.MarkReached();
                    break;
                case ZellerTerminalEventKind.Processing:
                    evidence.MarkProcessing(_time.GetUtcNow());
                    processing.TrySetResult();
                    break;
            }

            if (progress is null)
            {
                return;
            }

            try
            {
                progress.Report(terminalEvent);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 界面回调出错不能影响收款调用。
                Log($"the progress callback threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        IZellerSdkOperation<T> operation;
        try
        {
            var terminal = await EnsureTerminalAsync(timings).ConfigureAwait(false);
            timings.MarkTerminalReady();

            // 中文注释：发起调用和登记事件回调放在界面线程的同一次调度里，事件不会在两者之间漏掉。
            // 计时：「请求调度」到 lambda 开始执行是界面线程的排队时间（界面忙时会变长）；
            // lambda 里 start() 的耗时是 SDK 的同步部分（首次调用时可能包含 WebView2 的创建）。
            operation = await _dispatcher.InvokeAsync(() =>
            {
                timings.MarkDispatchStarted();
                var created = start(terminal);
                created.OnEvent(OnEvent);
                timings.MarkIssued();
                return created;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"the call could not be started: {ex.GetType().Name}: {ex.Message}");
            return GatewayRun<T>.StartFailed($"{ex.GetType().Name}: {ex.Message}");
        }

        var callerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() =>
        {
            evidence.MarkCancelRequested(_time.GetUtcNow());
            callerCancelled.TrySetResult();
            _ = SafeCancelAsync(operation, "the caller cancelled");
        });

        using var timers = new CancellationTokenSource();
        var completion = operation.Completion;
        var waits = new List<Task>(5) { completion, abandon.Task, callerCancelled.Task };
        if (limits.Total is { } total)
        {
            waits.Add(DelayUntilAsync(startedAt + total, timers.Token));
        }

        if (limits.AfterProcessing is { } afterProcessing)
        {
            waits.Add(DelayAfterProcessingAsync(processing.Task, evidence, afterProcessing, timers.Token));
        }

        var first = await Task.WhenAny(waits).ConfigureAwait(false);
        timers.Cancel();

        if (ReferenceEquals(first, completion))
        {
            return await CollectAsync(completion).ConfigureAwait(false);
        }

        if (ReferenceEquals(first, abandon.Task))
        {
            Log("the call was abandoned on request");
            return GatewayRun<T>.Abandoned();
        }

        DateTimeOffset graceStart;
        if (ReferenceEquals(first, callerCancelled.Task))
        {
            graceStart = evidence.CancelRequestedAt ?? _time.GetUtcNow();
        }
        else
        {
            Log(evidence.Processing
                ? "watchdog: no result after the terminal started processing, cancelling"
                : "watchdog: no result within the transaction timeout, cancelling");
            graceStart = _time.GetUtcNow();
            await SafeCancelAsync(operation, "the watchdog fired").ConfigureAwait(false);
        }

        // 中文注释：不管是看门狗还是调用方取消，都只等 CancelGrace。正常取消约 1 秒就会返回。
        using var graceTimer = new CancellationTokenSource();
        var settled = await Task.WhenAny(completion, abandon.Task, DelayUntilAsync(graceStart + _options.CancelGrace, graceTimer.Token))
            .ConfigureAwait(false);
        graceTimer.Cancel();
        if (ReferenceEquals(settled, completion))
        {
            return await CollectAsync(completion).ConfigureAwait(false);
        }

        Log("the Zeller SDK did not answer after the cancel request; abandoning the call");
        return GatewayRun<T>.Abandoned();
    }

    private async Task<IZellerSdkTerminal> EnsureTerminalAsync(CallTimings timings)
    {
        if (_terminal is { } existing)
        {
            return existing;
        }

        // 中文注释：这次调用要新建 Terminal（程序刚启动后的第一次、或上次作废之后），在耗时日志里标成冷启动。
        timings.MarkCold();
        var created = await _dispatcher.InvokeAsync(_factory.Create).ConfigureAwait(false);
        _terminal = created;
        var number = Interlocked.Increment(ref _terminalsCreated);
        Log($"created Zeller terminal #{number}");
        return created;
    }

    /// <summary>作废当前的 Terminal，下一次调用时重新创建。必须在持有串行门时调用。</summary>
    private async Task RecycleAsync(string reason)
    {
        var old = _terminal;
        _terminal = null;
        if (old is null)
        {
            return;
        }

        Log($"recycling the Zeller terminal: {reason}");
        try
        {
            await _dispatcher.InvokeAsync(old.Dispose).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"disposing the old terminal failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task SafeCancelAsync<T>(IZellerSdkOperation<T> operation, string why)
    {
        try
        {
            await _dispatcher.InvokeAsync(operation.Cancel).ConfigureAwait(false);
            Log($"cancel() sent: {why}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"cancel() threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SignalAbandon()
    {
        TaskCompletionSource? current;
        lock (_stateLock)
        {
            current = _abandon;
        }

        current?.TrySetResult();
    }

    private async Task DelayAfterProcessingAsync(
        Task processingStarted,
        CallEvidence evidence,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await processingStarted.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startedAt = evidence.ProcessingAt ?? _time.GetUtcNow();
        await DelayUntilAsync(startedAt + delay, cancellationToken).ConfigureAwait(false);
    }

    private Task DelayUntilAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var remaining = deadline - _time.GetUtcNow();
        return remaining > TimeSpan.Zero
            ? Task.Delay(remaining, _time, cancellationToken)
            : Task.CompletedTask;
    }

    private static async Task<GatewayRun<T>> CollectAsync<T>(Task<T> completion)
    {
        try
        {
            return GatewayRun<T>.Completed(await completion.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return GatewayRun<T>.Faulted($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ───────────── 结果转换 ─────────────

    private static bool IsBrokenSdkState(ZellerErrorType? errorType) =>
        errorType is ZellerErrorType.OperationBusy or ZellerErrorType.OperationResponseInvalid;

    private static bool LeavesBrokenSdkState(ZellerCallResult result) =>
        result.Outcome == ZellerCallOutcome.Error && IsBrokenSdkState(result.ErrorType);

    private static ZellerCallResult ToCallResult(GatewayRun<ZellerCallResult> run, CallEvidence evidence)
    {
        var processing = evidence.Processing;
        var reached = evidence.Reached || processing;
        return run.Kind switch
        {
            RunKind.Completed => run.Value! with
            {
                ReachedTerminal = run.Value.ReachedTerminal || reached,
                ProcessingStarted = run.Value.ProcessingStarted || processing
            },
            RunKind.NotStarted => ZellerCallResult.NotStarted(ZellerErrorType.Cancelled),
            RunKind.StartFailed => ZellerCallResult.NotStarted(message: run.Message),
            RunKind.Abandoned => ZellerCallResult.Abandoned(reached, processing),
            _ => ZellerCallResult.Faulted(run.Message ?? "The Zeller SDK call faulted.", reached, processing)
        };
    }

    private static ZellerPairingResult ToPairingResult(GatewayRun<ZellerPairingResult> run, string operation) => run.Kind switch
    {
        RunKind.Completed => run.Value!,
        RunKind.NotStarted => new ZellerPairingResult(false, ZellerErrorType.Cancelled, "Cancelled before the call reached the Zeller SDK."),
        RunKind.Abandoned => new ZellerPairingResult(false, ZellerErrorType.OperationFailure, $"The Zeller SDK did not answer {operation}."),
        _ => new ZellerPairingResult(false, ZellerErrorType.OperationFailure, run.Message)
    };

    private void Log(string message) => _log?.Invoke(message);

    /// <summary>每次调用结束时写一行耗时汇总；调用方在排队时就取消的也写（那段排队时间正是要看的）。</summary>
    private void LogTiming(string operationName, string result, CallTimings timings, string? blockedBy, TimeSpan recycleElapsed)
    {
        timings.MarkFinished();
        Log(timings.Describe(operationName, result, blockedBy, recycleElapsed));
    }

    // ───────────── 内部类型 ─────────────

    private readonly record struct CallLimits(TimeSpan? Total, TimeSpan? AfterProcessing);

    private enum RunKind
    {
        Completed,
        NotStarted,
        StartFailed,
        Abandoned,
        Faulted
    }

    private sealed record GatewayRun<T>(RunKind Kind, T? Value = default, string? Message = null)
    {
        public static GatewayRun<T> Completed(T value) => new(RunKind.Completed, value);

        public static GatewayRun<T> NotStarted() => new(RunKind.NotStarted);

        public static GatewayRun<T> StartFailed(string message) => new(RunKind.StartFailed, Message: message);

        public static GatewayRun<T> Abandoned() => new(RunKind.Abandoned);

        public static GatewayRun<T> Faulted(string message) => new(RunKind.Faulted, Message: message);
    }

    /// <summary>一次 SDK 调用各环节的时间点，只用来写诊断日志，不参与任何判断。</summary>
    /// <remarks>
    /// 中文注释：用来回答「点击 Zeller 到刷卡机显示金额」慢在哪一段，各项依次是：
    /// gateWaitMs（排队等串行门）→ terminalMs（新建 Terminal，冷启动才有）→ uiQueueMs（界面线程排队）
    /// → sdkStartMs（SDK 的同步部分）→ sdkToAwaitingMs（SDK 调用发出到刷卡机提示刷卡 = 云端中转 + 刷卡机）。
    /// promptAtMs（刷卡机提示刷卡）、processingAtMs、totalMs 都是从调用方发起算起；取不到的环节写 "-"。
    /// 键名不能含 card / pan / pin / token / secret 等片段：服务端入库时按键名抹值（ApplicationLogService.SensitiveKeyFragments），
    /// 曾因 awaitingCardAtMs 含 card 在中心日志里整个值变成 [REDACTED]（2026-11-11 实测发现）。
    /// 所有时间点取自注入的 TimeProvider（测试里是假时钟）。事件可能在任意线程上到达，所以每个时间点都只记第一次。
    /// </remarks>
    private sealed class CallTimings(TimeProvider time)
    {
        private const long Unset = long.MinValue;
        private readonly long _requested = time.GetTimestamp();
        private long _gateAcquired = Unset;
        private long _terminalReady = Unset;
        private long _dispatchStarted = Unset;
        private long _issued = Unset;
        private long _awaitingCard = Unset;
        private long _processing = Unset;
        private long _finished = Unset;
        private volatile bool _cold;

        public void MarkCold() => _cold = true;

        public void MarkGateAcquired() => Mark(ref _gateAcquired);

        public void MarkTerminalReady() => Mark(ref _terminalReady);

        public void MarkDispatchStarted() => Mark(ref _dispatchStarted);

        public void MarkIssued() => Mark(ref _issued);

        public void MarkFinished() => Mark(ref _finished);

        public void MarkEvent(ZellerTerminalEventKind kind)
        {
            switch (kind)
            {
                case ZellerTerminalEventKind.AwaitingPaymentMethod:
                    Mark(ref _awaitingCard);
                    break;
                case ZellerTerminalEventKind.Processing:
                    Mark(ref _processing);
                    break;
            }
        }

        /// <summary>从调用方发起到现在，毫秒。</summary>
        public long SinceRequestMs() => ToMs(_requested, time.GetTimestamp());

        public string Describe(string operationName, string result, string? blockedBy, TimeSpan recycleElapsed)
        {
            var gateAcquired = Read(ref _gateAcquired);
            var terminalReady = Read(ref _terminalReady);
            var dispatchStarted = Read(ref _dispatchStarted);
            var issued = Read(ref _issued);
            var awaiting = Read(ref _awaitingCard);
            var processing = Read(ref _processing);
            var finished = Read(ref _finished);
            var cold = _cold ? "yes" : "no";

            // 事件可能在 SDK 调用刚发出的同一时刻到达，时间点顺序略有颠倒时按 0 计，不会出现负数。
            return $"timing {operationName} result={result} cold={cold} blockedBy={blockedBy ?? "-"} " +
                $"gateWaitMs={Between(_requested, gateAcquired)} terminalMs={Between(gateAcquired, terminalReady)} " +
                $"uiQueueMs={Between(terminalReady, dispatchStarted)} sdkStartMs={Between(dispatchStarted, issued)} " +
                $"issuedAtMs={Between(_requested, issued)} promptAtMs={Between(_requested, awaiting)} " +
                $"processingAtMs={Between(_requested, processing)} sdkToAwaitingMs={Between(issued, awaiting)} " +
                $"recycleMs={FormatMs(Math.Max(0L, (long)Math.Round(recycleElapsed.TotalMilliseconds)))} " +
                $"totalMs={Between(_requested, finished)}";
        }

        // 只记第一次：重复的事件或重复的标记不能改写已经记下的时间点。
        private void Mark(ref long slot) => Interlocked.CompareExchange(ref slot, time.GetTimestamp(), Unset);

        private static long Read(ref long slot) => Interlocked.Read(ref slot);

        private long ToMs(long from, long to) => Math.Max(0L, (long)Math.Round(time.GetElapsedTime(from, to).TotalMilliseconds));

        private string Between(long from, long to) => from == Unset || to == Unset ? "-" : FormatMs(ToMs(from, to));

        private static string FormatMs(long milliseconds) => milliseconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>调用期间收集的证据和时间点。事件和取消可能在任意线程上到达。</summary>
    private sealed class CallEvidence
    {
        private volatile bool _reached;
        private long _processingTicks;
        private long _cancelRequestedTicks;

        public bool Reached => _reached;

        public bool Processing => Interlocked.Read(ref _processingTicks) != 0;

        public DateTimeOffset? ProcessingAt => FromTicks(Interlocked.Read(ref _processingTicks));

        public DateTimeOffset? CancelRequestedAt => FromTicks(Interlocked.Read(ref _cancelRequestedTicks));

        public void MarkReached() => _reached = true;

        // 只记第一次：重复的「处理中」事件不能把时限往后推。
        public void MarkProcessing(DateTimeOffset at) =>
            Interlocked.CompareExchange(ref _processingTicks, Math.Max(1, at.UtcTicks), 0);

        public void MarkCancelRequested(DateTimeOffset at) =>
            Interlocked.CompareExchange(ref _cancelRequestedTicks, Math.Max(1, at.UtcTicks), 0);

        private static DateTimeOffset? FromTicks(long ticks) =>
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
