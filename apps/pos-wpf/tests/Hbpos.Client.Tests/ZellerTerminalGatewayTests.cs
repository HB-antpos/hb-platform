using Hbpos.Client.Wpf.Services.Zeller;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// Zeller 网关：严格串行、取消只对进行中的调用有效、看门狗、作废并重建 Terminal。
/// 每条用例对应 2026-11-05 真机验证里的一个现象：SDK 0.1.0 在扣款进行中再收到任何调用就会丢结果，
/// 调用可能永远不返回，cancel() 不认调用。这些行为都用假 SDK 重现。
/// </summary>
public sealed class ZellerTerminalGatewayTests
{
    private const string Reference = "HBZ-799287a33dc24df1bca3d33eea19b35a";
    private static readonly ZellerPurchaseRequest Request = new(100, Reference);
    private static readonly ZellerTransaction Approved =
        new(ZellerTransactionStatus.Approved, ZellerTransactionType.Purchase, 100, "65c4b343-03e6-49ef-a54d-012e3f2c222c", Reference);

    // ── 正常调用 ──

    [Fact]
    public async Task Purchase_returns_the_sdk_result_and_adds_the_event_evidence()
    {
        using var harness = new Harness();
        var events = new SafeList<ZellerTerminalEvent>();

        var purchase = harness.Gateway.PurchaseAsync(Request, new SyncProgress<ZellerTerminalEvent>(events.Add));
        var operation = await harness.NextPurchaseAsync();
        operation.Emit(ZellerTerminalEventKind.AwaitingPaymentMethod);
        operation.Emit(ZellerTerminalEventKind.Processing);
        // 真实 SDK 返回的错误里不带事件信息，网关要把调用期间看到的事件补上。
        operation.Complete(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure));
        var result = await purchase;

        Assert.Equal(ZellerCallOutcome.Error, result.Outcome);
        Assert.Equal(ZellerErrorType.NetworkFailure, result.ErrorType);
        Assert.True(result.ReachedTerminal);
        Assert.True(result.ProcessingStarted);
        Assert.Equal(
            [ZellerTerminalEventKind.AwaitingPaymentMethod, ZellerTerminalEventKind.Processing],
            events.ToArray().Select(terminalEvent => terminalEvent.Kind));
    }

    [Fact]
    public async Task Error_before_the_terminal_prompted_for_a_card_has_no_terminal_evidence()
    {
        using var harness = new Harness();

        var purchase = harness.Gateway.PurchaseAsync(Request);
        (await harness.NextPurchaseAsync()).Complete(ZellerCallResult.FromError(ZellerErrorType.ConnectionFailure));
        var result = await purchase;

        Assert.False(result.ReachedTerminal);
        Assert.False(result.ProcessingStarted);
    }

    [Fact]
    public async Task Terminal_is_created_lazily_once_and_reused_after_normal_calls()
    {
        using var harness = new Harness();
        Assert.Equal(0, harness.Gateway.TerminalsCreated);

        var purchase = harness.Gateway.PurchaseAsync(Request);
        (await harness.NextPurchaseAsync()).Complete(ZellerCallResult.FromTransaction(Approved));
        await purchase;
        var query = harness.Gateway.GetTransactionsAsync(Reference);
        (await harness.NextQueryAsync()).Complete(ZellerQueryResult.Found(Approved));
        var found = await query;

        Assert.True(found.Succeeded);
        Assert.Single(found.Transactions);
        Assert.Equal(1, harness.Gateway.TerminalsCreated);
        Assert.Single(harness.Sdk.Terminals);
        Assert.False(harness.Sdk.Current.Disposed);
        Assert.Equal([Reference], harness.Sdk.Current.QueryReferences.ToArray());
    }

    [Fact]
    public async Task Initialise_and_setup_return_the_pairing_result()
    {
        using var harness = new Harness();

        var initialise = harness.Gateway.InitialiseAsync();
        await WaitUntilAsync(() => harness.Sdk.Terminals.Count == 1 && harness.Sdk.Current.Initialises.Count == 1);
        harness.Sdk.Current.Initialises[0].Complete(new ZellerPairingResult(true));
        var setup = harness.Gateway.SetupAsync();
        await WaitUntilAsync(() => harness.Sdk.Current.Setups.Count == 1);
        harness.Sdk.Current.Setups[0].Complete(new ZellerPairingResult(true));

        Assert.True((await initialise).IsPaired);
        Assert.True((await setup).IsPaired);
    }

    [Fact]
    public async Task Events_still_flow_when_the_progress_callback_throws()
    {
        using var harness = new Harness();

        var purchase = harness.Gateway.PurchaseAsync(
            Request,
            new SyncProgress<ZellerTerminalEvent>(_ => throw new InvalidOperationException("UI is gone")));
        var operation = await harness.NextPurchaseAsync();
        operation.Emit(ZellerTerminalEventKind.Processing);
        operation.Complete(ZellerCallResult.FromTransaction(Approved));
        var result = await purchase;

        Assert.Equal(ZellerCallOutcome.Transaction, result.Outcome);
        Assert.True(result.ProcessingStarted);
        Assert.Contains(harness.LogLines, line => line.Contains("progress callback threw", StringComparison.Ordinal));
    }

    // ── 严格串行 ──

    [Fact]
    public async Task Query_waits_until_the_purchase_has_returned()
    {
        // 真机：扣款进行中发起查询，查询返回 OperationBusy，扣款的结果从此丢失。
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        var operation = await harness.NextPurchaseAsync();

        var query = harness.Gateway.GetTransactionsAsync(Reference);
        await Task.Delay(100);
        Assert.Equal(0, harness.Sdk.Current.Queries.Count);
        Assert.True(harness.Gateway.IsBusy);

        operation.Complete(ZellerCallResult.FromTransaction(Approved));
        await purchase;
        (await harness.NextQueryAsync()).Complete(ZellerQueryResult.Found(Approved));
        await query;

        Assert.Equal(1, harness.Sdk.MaxConcurrentCalls);
        Assert.False(harness.Gateway.IsBusy);
    }

    [Fact]
    public async Task Many_concurrent_callers_never_put_two_calls_into_the_sdk()
    {
        using var harness = new Harness();
        harness.Sdk.QueryResponder = _ => ZellerQueryResult.Found();
        harness.Sdk.PurchaseResponder = request => ZellerCallResult.FromTransaction(
            Approved with { ExternalReference = request.Reference });

        var calls = Enumerable.Range(0, 30)
            .Select(index => index % 5 == 0
                ? (Task)Task.Run(() => harness.Gateway.PurchaseAsync(new ZellerPurchaseRequest(100, $"HBZ-{Guid.NewGuid():N}")))
                : Task.Run(() => harness.Gateway.GetTransactionsAsync(Reference)))
            .ToArray();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, harness.Sdk.MaxConcurrentCalls);
        Assert.Equal(1, harness.Gateway.TerminalsCreated);
        Assert.Equal(24, harness.Sdk.Current.Queries.Count);
        Assert.Equal(6, harness.Sdk.Current.Purchases.Count);
    }

    [Fact]
    public async Task Queued_query_cancelled_while_waiting_never_reaches_the_sdk()
    {
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        var operation = await harness.NextPurchaseAsync();
        using var cancellation = new CancellationTokenSource();

        var query = harness.Gateway.GetTransactionsAsync(Reference, cancellation.Token);
        cancellation.Cancel();
        var result = await query;
        operation.Complete(ZellerCallResult.FromTransaction(Approved));
        await purchase;

        Assert.False(result.Succeeded);
        Assert.Equal(ZellerErrorType.Cancelled, result.ErrorType);
        Assert.Equal(0, harness.Sdk.Current.Queries.Count);
        Assert.Equal(0, operation.CancelCount);
    }

    [Fact]
    public async Task Queued_purchase_cancelled_while_waiting_is_reported_as_not_started()
    {
        using var harness = new Harness();
        var first = harness.Gateway.PurchaseAsync(Request);
        var operation = await harness.NextPurchaseAsync();
        using var cancellation = new CancellationTokenSource();

        var second = harness.Gateway.PurchaseAsync(new ZellerPurchaseRequest(100, "HBZ-00000000000000000000000000000002"), cancellationToken: cancellation.Token);
        cancellation.Cancel();
        var result = await second;
        operation.Complete(ZellerCallResult.FromTransaction(Approved));
        await first;

        Assert.Equal(ZellerCallOutcome.NotStarted, result.Outcome);
        Assert.Equal(ZellerErrorType.Cancelled, result.ErrorType);
        Assert.Equal(1, harness.Sdk.Current.Purchases.Count);
        var classification = ZellerResultClassifier.ClassifyCall(
            result,
            new ZellerAttemptExpectation(ZellerTransactionType.Purchase, 100, "HBZ-00000000000000000000000000000002"));
        Assert.Equal(ZellerCallDecision.NotSubmitted, classification.Decision);
        Assert.Equal(ZellerFailureKind.Cancelled, classification.FailureKind);
    }

    [Fact]
    public async Task Purchase_cancelled_before_it_starts_never_reaches_the_sdk()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await harness.Gateway.PurchaseAsync(Request, cancellationToken: cancellation.Token);

        Assert.Equal(ZellerCallOutcome.NotStarted, result.Outcome);
        Assert.Empty(harness.Sdk.Terminals);
    }

    // ── 取消 ──

    [Fact]
    public async Task Cancelling_an_in_flight_purchase_cancels_only_that_call()
    {
        // 真机：在已结束的调用上调 cancel()，被取消的是当时正在进行或下一个调用。
        using var harness = new Harness();
        using var firstCancellation = new CancellationTokenSource();
        var first = harness.Gateway.PurchaseAsync(Request, cancellationToken: firstCancellation.Token);
        var firstOperation = await harness.NextPurchaseAsync();

        firstCancellation.Cancel();
        await WaitUntilAsync(() => firstOperation.CancelCount == 1);
        firstOperation.Complete(ZellerCallResult.FromError(ZellerErrorType.Cancelled));
        var firstResult = await first;

        var second = harness.Gateway.PurchaseAsync(new ZellerPurchaseRequest(100, "HBZ-00000000000000000000000000000002"));
        var secondOperation = await harness.NextPurchaseAsync(index: 1);
        firstCancellation.Cancel();
        await Task.Delay(50);
        secondOperation.Complete(ZellerCallResult.FromError(ZellerErrorType.InactivityTimeout));
        await second;

        Assert.Equal(ZellerErrorType.Cancelled, firstResult.ErrorType);
        Assert.Equal(1, firstOperation.CancelCount);
        Assert.Equal(0, secondOperation.CancelCount);
        Assert.False(harness.Sdk.Current.Disposed);
    }

    [Fact]
    public async Task Cancelled_call_that_never_answers_is_abandoned_after_the_grace_period()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        var purchase = harness.Gateway.PurchaseAsync(Request, cancellationToken: cancellation.Token);
        var operation = await harness.NextPurchaseAsync();

        cancellation.Cancel();
        await WaitUntilAsync(() => operation.CancelCount == 1);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.False(purchase.IsCompleted);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        var result = await purchase.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ZellerCallOutcome.Abandoned, result.Outcome);
        Assert.True(harness.Sdk.Terminals[0].Disposed);
    }

    // ── 看门狗 ──

    [Fact]
    public async Task Watchdog_cancels_at_the_transaction_timeout_and_abandons_after_the_grace_period()
    {
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        var operation = await harness.NextPurchaseAsync();
        operation.Emit(ZellerTerminalEventKind.AwaitingPaymentMethod);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(145), TimeSpan.FromSeconds(5));
        Assert.Equal(0, operation.CancelCount);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => operation.CancelCount == 1);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.False(purchase.IsCompleted);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        var result = await purchase.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ZellerCallOutcome.Abandoned, result.Outcome);
        Assert.True(result.ReachedTerminal);
        Assert.False(result.ProcessingStarted);
        Assert.True(harness.Sdk.Terminals[0].Disposed);
        Assert.False(harness.Gateway.IsBusy);
    }

    [Fact]
    public async Task Abandoned_call_is_followed_by_a_fresh_terminal_so_the_next_query_works()
    {
        // 真机 V3：看门狗接手 → 重建 → 新实例上的查询和扣款都正常。
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        await harness.NextPurchaseAsync();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(5));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ZellerCallOutcome.Abandoned, (await purchase.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);

        var query = harness.Gateway.GetTransactionsAsync(Reference);
        await WaitUntilAsync(() => harness.Sdk.Terminals.Count == 2 && harness.Sdk.Current.Queries.Count == 1);
        harness.Sdk.Current.Queries[0].Complete(ZellerQueryResult.Found(Approved));
        var found = await query;

        Assert.True(found.Succeeded);
        Assert.Equal(2, harness.Gateway.TerminalsCreated);
        Assert.Equal(0, harness.Sdk.Terminals[0].Queries.Count);
        Assert.Equal(1, harness.Sdk.MaxConcurrentCalls);
    }

    [Fact]
    public async Task Watchdog_returns_the_sdk_answer_when_cancel_works_within_the_grace_period()
    {
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        var operation = await harness.NextPurchaseAsync();
        operation.OnCancel = () => operation.Complete(ZellerCallResult.FromError(ZellerErrorType.Cancelled));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(5));
        var result = await purchase.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ZellerCallOutcome.Error, result.Outcome);
        Assert.Equal(ZellerErrorType.Cancelled, result.ErrorType);
        Assert.False(harness.Sdk.Terminals[0].Disposed);
    }

    [Fact]
    public async Task Processing_starts_a_shorter_watchdog()
    {
        using var harness = new Harness(new ZellerGatewayOptions { TransactionTimeout = TimeSpan.FromMinutes(10) });
        var purchase = harness.Gateway.PurchaseAsync(Request);
        var operation = await harness.NextPurchaseAsync();

        await harness.AdvanceAsync(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(30));
        Assert.Equal(0, operation.CancelCount);
        operation.Emit(ZellerTerminalEventKind.Processing);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(5));
        Assert.Equal(0, operation.CancelCount);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => operation.CancelCount == 1);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(3));
        var result = await purchase.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ZellerCallOutcome.Abandoned, result.Outcome);
        Assert.True(result.ProcessingStarted);
    }

    [Fact]
    public async Task Query_that_never_answers_fails_and_recycles_the_terminal()
    {
        using var harness = new Harness();
        var query = harness.Gateway.GetTransactionsAsync(Reference);
        var operation = await harness.NextQueryAsync();

        await harness.AdvanceAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => operation.CancelCount == 1);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(3));
        var result = await query.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Succeeded);
        Assert.Equal(ZellerErrorType.OperationFailure, result.ErrorType);
        Assert.True(harness.Sdk.Terminals[0].Disposed);
    }

    [Fact]
    public async Task Setup_waits_for_the_manager_without_a_total_timeout()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        var setup = harness.Gateway.SetupAsync(cancellation.Token);
        await WaitUntilAsync(() => harness.Sdk.Terminals.Count == 1 && harness.Sdk.Current.Setups.Count == 1);
        var operation = harness.Sdk.Current.Setups[0];

        await harness.AdvanceAsync(TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
        Assert.False(setup.IsCompleted);
        Assert.Equal(0, operation.CancelCount);

        cancellation.Cancel();
        await WaitUntilAsync(() => operation.CancelCount == 1);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(3));
        var result = await setup.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsPaired);
        Assert.Equal(ZellerErrorType.OperationFailure, result.ErrorType);
    }

    // ── SDK 状态被破坏时重建 ──

    [Fact]
    public async Task Faulted_call_is_reported_and_the_terminal_is_recycled()
    {
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        var operation = await harness.NextPurchaseAsync();
        operation.Emit(ZellerTerminalEventKind.AwaitingPaymentMethod);

        operation.Fail(new InvalidOperationException("webview crashed"));
        var result = await purchase;

        Assert.Equal(ZellerCallOutcome.Faulted, result.Outcome);
        Assert.Contains("webview crashed", result.Message, StringComparison.Ordinal);
        Assert.True(result.ReachedTerminal);
        Assert.True(harness.Sdk.Terminals[0].Disposed);
    }

    [Theory]
    [InlineData(ZellerErrorType.OperationBusy)]
    [InlineData(ZellerErrorType.OperationResponseInvalid)]
    public async Task Sdk_errors_that_mean_its_state_is_broken_recycle_the_terminal(ZellerErrorType errorType)
    {
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        (await harness.NextPurchaseAsync()).Complete(ZellerCallResult.FromError(errorType));
        var result = await purchase;
        var query = harness.Gateway.GetTransactionsAsync(Reference);
        await WaitUntilAsync(() => harness.Sdk.Terminals.Count == 2 && harness.Sdk.Current.Queries.Count == 1);
        harness.Sdk.Current.Queries[0].Complete(ZellerQueryResult.Found());
        await query;

        Assert.Equal(errorType, result.ErrorType);
        Assert.True(harness.Sdk.Terminals[0].Disposed);
        Assert.False(harness.Sdk.Terminals[1].Disposed);
    }

    [Fact]
    public async Task Query_reporting_busy_also_recycles_the_terminal()
    {
        using var harness = new Harness();
        var query = harness.Gateway.GetTransactionsAsync(Reference);
        (await harness.NextQueryAsync()).Complete(ZellerQueryResult.Failed(ZellerErrorType.OperationBusy));
        var result = await query;

        Assert.False(result.Succeeded);
        Assert.True(harness.Sdk.Terminals[0].Disposed);
    }

    [Theory]
    [InlineData(ZellerErrorType.Cancelled)]
    [InlineData(ZellerErrorType.InactivityTimeout)]
    [InlineData(ZellerErrorType.NetworkFailure)]
    [InlineData(ZellerErrorType.ConnectionFailure)]
    public async Task Ordinary_errors_keep_the_terminal(ZellerErrorType errorType)
    {
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        (await harness.NextPurchaseAsync()).Complete(ZellerCallResult.FromError(errorType));
        await purchase;

        Assert.False(harness.Sdk.Terminals[0].Disposed);
        Assert.Equal(1, harness.Gateway.TerminalsCreated);
    }

    // ── 手动重建与关闭 ──

    [Fact]
    public async Task Reset_abandons_the_in_flight_call_and_the_next_call_uses_a_new_terminal()
    {
        using var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        var operation = await harness.NextPurchaseAsync();
        operation.Emit(ZellerTerminalEventKind.AwaitingPaymentMethod);

        await harness.Gateway.ResetAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var result = await purchase.WaitAsync(TimeSpan.FromSeconds(5));
        var query = harness.Gateway.GetTransactionsAsync(Reference);
        await WaitUntilAsync(() => harness.Sdk.Terminals.Count == 2 && harness.Sdk.Current.Queries.Count == 1);
        harness.Sdk.Current.Queries[0].Complete(ZellerQueryResult.Found());
        await query;

        Assert.Equal(ZellerCallOutcome.Abandoned, result.Outcome);
        Assert.True(result.ReachedTerminal);
        Assert.True(harness.Sdk.Terminals[0].Disposed);
        Assert.Equal(1, harness.Sdk.MaxConcurrentCalls);
    }

    [Fact]
    public async Task Reset_when_idle_disposes_the_terminal_and_the_next_call_creates_another()
    {
        using var harness = new Harness();
        var query = harness.Gateway.GetTransactionsAsync(Reference);
        (await harness.NextQueryAsync()).Complete(ZellerQueryResult.Found());
        await query;

        await harness.Gateway.ResetAsync();
        var second = harness.Gateway.GetTransactionsAsync(Reference);
        await WaitUntilAsync(() => harness.Sdk.Terminals.Count == 2 && harness.Sdk.Current.Queries.Count == 1);
        harness.Sdk.Current.Queries[0].Complete(ZellerQueryResult.Found());
        await second;

        Assert.True(harness.Sdk.Terminals[0].Disposed);
        Assert.Equal(2, harness.Gateway.TerminalsCreated);
    }

    [Fact]
    public async Task Dispose_abandons_the_in_flight_call()
    {
        var harness = new Harness();
        var purchase = harness.Gateway.PurchaseAsync(Request);
        await harness.NextPurchaseAsync();

        harness.Gateway.Dispose();
        var result = await purchase.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ZellerCallOutcome.Abandoned, result.Outcome);
        Assert.True(harness.Sdk.Terminals[0].Disposed);
    }

    // ── SDK 起不来 ──

    [Fact]
    public async Task Terminal_creation_failure_is_reported_as_not_started_and_frees_the_gate()
    {
        using var harness = new Harness();
        harness.Sdk.CreateFailure = new InvalidOperationException("WebView2 runtime is missing");

        var failed = await harness.Gateway.PurchaseAsync(Request);
        harness.Sdk.CreateFailure = null;
        var purchase = harness.Gateway.PurchaseAsync(Request);
        (await harness.NextPurchaseAsync()).Complete(ZellerCallResult.FromTransaction(Approved));
        var result = await purchase;

        Assert.Equal(ZellerCallOutcome.NotStarted, failed.Outcome);
        Assert.Null(failed.ErrorType);
        Assert.Contains("WebView2 runtime is missing", failed.Message, StringComparison.Ordinal);
        Assert.Equal(
            ZellerFailureKind.SdkUnavailable,
            ZellerResultClassifier.ClassifyCall(failed, new ZellerAttemptExpectation(ZellerTransactionType.Purchase, 100, Reference)).FailureKind);
        Assert.Equal(ZellerCallOutcome.Transaction, result.Outcome);
    }

    [Fact]
    public async Task Sdk_throwing_when_the_call_is_started_is_reported_as_not_started()
    {
        using var harness = new Harness();
        var warmUp = harness.Gateway.GetTransactionsAsync(Reference);
        (await harness.NextQueryAsync()).Complete(ZellerQueryResult.Found());
        await warmUp;
        harness.Sdk.Current.PurchaseThrows = new ArgumentException("amount is invalid");

        var result = await harness.Gateway.PurchaseAsync(Request);

        Assert.Equal(ZellerCallOutcome.NotStarted, result.Outcome);
        Assert.Contains("amount is invalid", result.Message, StringComparison.Ordinal);
        Assert.False(harness.Gateway.IsBusy);
    }

    // ── 参数 ──

    public static TheoryData<ZellerGatewayOptions> InvalidOptions => new()
    {
        new ZellerGatewayOptions { TransactionTimeout = TimeSpan.Zero },
        new ZellerGatewayOptions { AfterProcessingTimeout = TimeSpan.FromSeconds(-1) },
        new ZellerGatewayOptions { CancelGrace = TimeSpan.Zero },
        new ZellerGatewayOptions { QueryTimeout = TimeSpan.Zero },
        new ZellerGatewayOptions { InitialiseTimeout = TimeSpan.Zero }
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Non_positive_timeouts_are_rejected(ZellerGatewayOptions options)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ZellerTerminalGateway(new FakeZellerSdk(), new ImmediateZellerDispatcher(), options));
    }

    [Fact]
    public async Task Blank_query_reference_is_rejected_before_the_gate()
    {
        using var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Gateway.GetTransactionsAsync(" "));
        Assert.Empty(harness.Sdk.Terminals);
    }

    // ── 辅助 ──

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("Condition was not met within 5 seconds.");
    }

    private sealed class Harness : IDisposable
    {
        private readonly SafeList<string> _log = new();

        public Harness(ZellerGatewayOptions? options = null)
        {
            Gateway = new ZellerTerminalGateway(Sdk, new ImmediateZellerDispatcher(), options, Time, _log.Add);
        }

        public FakeZellerSdk Sdk { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public ZellerTerminalGateway Gateway { get; }

        public string[] LogLines => _log.ToArray();

        public async Task<FakeZellerOperation<ZellerCallResult>> NextPurchaseAsync(int index = 0)
        {
            await WaitUntilAsync(() => Sdk.Terminals.Count > 0 && Sdk.Current.Purchases.Count > index);
            return Sdk.Current.Purchases[index];
        }

        public async Task<FakeZellerOperation<ZellerQueryResult>> NextQueryAsync(int index = 0)
        {
            await WaitUntilAsync(() => Sdk.Terminals.Count > 0 && Sdk.Current.Queries.Count > index);
            return Sdk.Current.Queries[index];
        }

        /// <summary>分步推进假时钟，每一步之后让网关有机会登记新的计时器。</summary>
        public async Task AdvanceAsync(TimeSpan total, TimeSpan? step = null)
        {
            var increment = step ?? TimeSpan.FromSeconds(1);
            var remaining = total;
            while (remaining > TimeSpan.Zero)
            {
                var next = remaining < increment ? remaining : increment;
                Time.Advance(next);
                remaining -= next;
                await Task.Delay(10);
            }
        }

        public void Dispose() => Gateway.Dispose();
    }
}
