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
        Func<IZellerSdkTerminal, IZellerSdkOperation<T>> start,
        CallLimits limits,
        IProgress<ZellerTerminalEvent>? progress,
        CallEvidence evidence,
        Func<T, bool> leavesBrokenSdkState,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GatewayRun<T>.NotStarted();
        }

        var abandon = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return GatewayRun<T>.NotStarted();
            }

            lock (_stateLock)
            {
                _abandon = abandon;
            }

            var run = await ExecuteAsync(start, limits, progress, evidence, abandon, cancellationToken).ConfigureAwait(false);
            var reason = run.Kind switch
            {
                RunKind.Abandoned => "the call was abandoned",
                RunKind.Faulted => "the call faulted",
                RunKind.Completed when leavesBrokenSdkState(run.Value!) => "the SDK reported a broken internal state",
                _ => null
            };
            if (reason is not null)
            {
                await RecycleAsync(reason).ConfigureAwait(false);
            }

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

            _gate.Release();
        }
    }

    private async Task<GatewayRun<T>> ExecuteAsync<T>(
        Func<IZellerSdkTerminal, IZellerSdkOperation<T>> start,
        CallLimits limits,
        IProgress<ZellerTerminalEvent>? progress,
        CallEvidence evidence,
        TaskCompletionSource abandon,
        CancellationToken cancellationToken)
    {
        // 中文注释：所有时限都从事件发生的那一刻算起，而不是从计时器登记的那一刻。
        // 计时器在线程池上登记，线程调度的延迟不能把看门狗的时限拉长。
        var startedAt = _time.GetUtcNow();
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnEvent(ZellerTerminalEvent terminalEvent)
        {
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
            var terminal = await EnsureTerminalAsync().ConfigureAwait(false);

            // 中文注释：发起调用和登记事件回调放在界面线程的同一次调度里，事件不会在两者之间漏掉。
            operation = await _dispatcher.InvokeAsync(() =>
            {
                var created = start(terminal);
                created.OnEvent(OnEvent);
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

    private async Task<IZellerSdkTerminal> EnsureTerminalAsync()
    {
        if (_terminal is { } existing)
        {
            return existing;
        }

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
