using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;
using static Hbpos.Client.Tests.PosTerminalRemoteLookupTestSupport;

namespace Hbpos.Client.Tests;

/// <summary>
/// C4：工作流向收银页暴露的扫码回调（PosScanHooks）与显式推进购物车世代（AdvanceCartGeneration）。
/// 这些是本次新增的 API，对照旧代码时本文件会因编译不过而整体失败，行为层面的失败对照见同系列的
/// PosTerminalRemoteLookupWorkflowTests / PosTerminalRemoteLookupViewModelTests。
/// </summary>
[Collection(ShutdownTimingTestCollection.Name)]
public sealed class PosTerminalRemoteLookupHooksTests
{
    [Fact]
    public async Task Interim_result_is_reported_synchronously_when_the_lookup_is_dispatched()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var interimResults = new List<PosTerminalWorkflowResult>();
        var hooks = new PosScanHooks { OnInterimResult = interimResults.Add };

        var scan = service.ProcessScanAsync(Session, "9306001", preferExactLookup: true, source: "raw", traceId: null, hooks: hooks);

        // 补查结果还没回来，回调就已经同步触发过一次：这是收银员「扫码的同一时刻」就能看到的反馈。
        var interim = Assert.Single(interimResults);
        Assert.Equal("pos.status.remoteLookupStarted", interim.StatusKey);
        Assert.False(scan.IsCompleted);

        remote.Found("9306001", CreateItem("SKU-C4-H01", "Remote Cola", "9306001", 3m));
        var result = await scan;

        // 最终的加购结果通过返回值给出，不会再走一次中间回调。
        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.Single(interimResults);
    }

    [Fact]
    public async Task Duplicate_scan_does_not_report_a_second_interim_result()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var firstInterim = new List<PosTerminalWorkflowResult>();
        var secondInterim = new List<PosTerminalWorkflowResult>();

        var first = service.ProcessScanAsync(
            Session, "9306002", preferExactLookup: true, source: "raw", traceId: null,
            hooks: new PosScanHooks { OnInterimResult = firstInterim.Add });
        var second = await service.ProcessScanAsync(
            Session, "9306002", preferExactLookup: true, source: "raw", traceId: null,
            hooks: new PosScanHooks { OnInterimResult = secondInterim.Add });

        // 重复扫码的提示是它自己的返回结果；不再触发「开始查询」回调，免得多响一次受理音。
        Assert.Equal("pos.status.remoteLookupInProgress", second.StatusKey);
        Assert.Empty(secondInterim);
        Assert.Single(firstInterim);

        remote.Found("9306002", CreateItem("SKU-C4-H02", "Remote Cola", "9306002", 3m));
        await first;
        Assert.Equal(1m, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public async Task Before_remote_apply_hook_runs_right_before_the_add_and_not_for_discarded_results()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var linesSeenByHook = new List<int>();
        var hooks = new PosScanHooks { OnBeforeRemoteApply = () => linesSeenByHook.Add(cart.Lines.Count) };

        var added = service.ProcessScanAsync(Session, "9306003", preferExactLookup: true, source: "raw", traceId: null, hooks: hooks);
        await remote.WaitForCallAsync("9306003");
        remote.Found("9306003", CreateItem("SKU-C4-H03", "Remote Cola", "9306003", 3m));
        await added;

        // 回调触发时这件商品还没进购物车：审计的「改前快照」取到的正是加购前的状态。
        Assert.Equal([0], linesSeenByHook);
        Assert.Single(cart.Lines);

        var discarded = service.ProcessScanAsync(Session, "9306004", preferExactLookup: true, source: "raw", traceId: null, hooks: hooks);
        await remote.WaitForCallAsync("9306004");
        service.AdvanceCartGeneration("test");
        remote.Found("9306004", CreateItem("SKU-C4-H04", "Remote Juice", "9306004", 5m));
        var discardedResult = await discarded;

        Assert.Equal("pos.status.remoteLookupDiscarded", discardedResult.StatusKey);
        Assert.Equal([0], linesSeenByHook);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Hook_failures_do_not_break_the_scan()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var hooks = new PosScanHooks
        {
            OnInterimResult = _ => throw new InvalidOperationException("interim hook failed"),
            OnBeforeRemoteApply = () => throw new InvalidOperationException("before-apply hook failed")
        };

        var scan = service.ProcessScanAsync(Session, "9306005", preferExactLookup: true, source: "raw", traceId: null, hooks: hooks);
        await remote.WaitForCallAsync("9306005");
        remote.Found("9306005", CreateItem("SKU-C4-H05", "Remote Cola", "9306005", 3m));
        var result = await scan;

        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.Equal(1m, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public async Task Hooks_are_not_invoked_for_local_hits_or_when_no_remote_lookup_runs()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var localItem = CreateItem("SKU-C4-H06", "Local Tea", "9306006", 2m);
        index.ReplaceAll([localItem]);
        var service = new PosTerminalWorkflowService(
            index,
            cart,
            (_, _, _) => throw new InvalidOperationException("Remote lookup should not run."));
        var calls = 0;
        var hooks = new PosScanHooks
        {
            OnInterimResult = _ => calls++,
            OnBeforeRemoteApply = () => calls++
        };

        var localHit = await service.ProcessScanAsync(Session, "9306006", preferExactLookup: true, source: "raw", traceId: null, hooks: hooks);
        var offlineMiss = await service.ProcessScanAsync(
            Session with { IsOnline = false }, "9306007", preferExactLookup: true, source: "raw", traceId: null, hooks: hooks);

        Assert.Equal("pos.status.added", localHit.StatusKey);
        Assert.Equal("pos.status.noLocalMatch", offlineMiss.StatusKey);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Advancing_the_cart_generation_discards_results_of_scans_started_before_it_only()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);

        var before = service.ProcessScanAsync(Session, "9306008", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9306008");
        service.AdvanceCartGeneration("session-changed");
        var after = service.ProcessScanAsync(Session, "9306009", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9306009");

        remote.Found("9306008", CreateItem("SKU-C4-H08", "Old Cola", "9306008", 3m));
        remote.Found("9306009", CreateItem("SKU-C4-H09", "New Juice", "9306009", 5m));
        var results = await Task.WhenAll(before, after);

        // 世代只作废「推进之前发起」的扫码；推进之后发起的扫码不受影响。
        Assert.Equal("pos.status.remoteLookupDiscarded", results[0].StatusKey);
        Assert.Equal("pos.status.added", results[1].StatusKey);
        Assert.Equal("New Juice", Assert.Single(cart.Lines).DisplayName);
    }
}

/// <summary>
/// C4：点收款时等待在途补查的预算。依赖本次新增的收银页 <c>timeProvider</c> 构造参数。
/// </summary>
[Collection(CultureSensitiveTestCollection.Name)]
public sealed class PosTerminalPaymentLookupWaitBudgetTests
{
    [Fact]
    public async Task Open_payment_stops_waiting_after_the_lookup_budget_when_the_remote_call_never_returns()
    {
        var viewModelClock = new FakeTimeProvider();
        using var harness = new TerminalHarness(viewModelTimeProvider: viewModelClock, remoteIgnoresCancellation: true);
        harness.Cart.AddItem(CreateItem("SKU-C4-B01", "Existing Tea", "9307001", 2m));

        harness.Scan("9307002");
        await harness.Remote.WaitForCallAsync("9307002");
        var payment = harness.OpenPaymentAsync();
        Assert.False(payment.IsCompleted, "补查进行中点收款应当先等补查结束。");

        // 补查自身卡死（回调不响应取消）：预算没用完前继续等，用完一个补查超时就照常收款，不会把收款永远拖住。
        viewModelClock.Advance(PosTerminalWorkflowService.RemoteLookupTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(payment.IsCompleted);
        viewModelClock.Advance(TimeSpan.FromMilliseconds(1));
        await payment.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.Equal([1], harness.LinesAtPayment);

        // 卡死的补查之后才回来：付款页已打开，结果按世代规则丢弃，不改动付款页上的购物车。
        harness.Remote.Found("9307002", CreateItem("SKU-C4-B02", "Late Water", "9307002", 1.5m));
        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));
        Assert.Equal("Existing Tea", Assert.Single(harness.Cart.Lines).DisplayName);
    }
}
