using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;
using static Hbpos.Client.Tests.PosTerminalRemoteLookupTestSupport;

namespace Hbpos.Client.Tests;

/// <summary>
/// C4：本地查不到商品、远程补查（最长 2 秒）期间的重复扫码与「购物车世代」丢弃规则。
/// 本文件只使用补查改动前就存在的公开 API（ProcessScanAsync / ClearCart / GuardPayment），
/// 便于在旧代码上直接跑出失败对照。
/// </summary>
[Collection(ShutdownTimingTestCollection.Name)]
public sealed class PosTerminalRemoteLookupWorkflowTests
{
    [Fact]
    public async Task Same_code_scanned_again_while_lookup_pending_returns_hint_and_adds_only_once()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var remoteItem = CreateItem("SKU-C4-01", "Remote Cola", "9304001", 3.25m);

        var first = service.ProcessScanAsync(Session, "9304001", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304001");
        var second = service.ProcessScanAsync(Session, "9304001", preferExactLookup: true, source: "raw");

        // 同码补查进行中再次扫到：不排队等待第一次的结果，立刻返回「正在查询」提示，且不加购。
        Assert.True(second.IsCompleted, "同码补查进行中的第二次扫码应立即返回，而不是再等一轮补查。");
        var secondResult = await second;
        Assert.Equal("pos.status.remoteLookupInProgress", secondResult.StatusKey);
        Assert.False(secondResult.ClearScanText);
        Assert.Empty(cart.Lines);

        remote.Found("9304001", remoteItem);
        var firstResult = await first;

        Assert.Equal("pos.status.added", firstResult.StatusKey);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(3.25m, cart.TotalAmount);
        Assert.Equal(1, remote.Calls("9304001"));
    }

    [Fact]
    public async Task Different_codes_pending_at_the_same_time_are_looked_up_and_added_independently()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var cola = CreateItem("SKU-C4-02", "Remote Cola", "9304002", 3m);
        var juice = CreateItem("SKU-C4-03", "Remote Juice", "9304003", 5m);

        var first = service.ProcessScanAsync(Session, "9304002", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304002");
        var second = service.ProcessScanAsync(Session, "9304003", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304003");

        // 不同码互不影响：第二个码不会被当成「正在查询」而吞掉。
        Assert.False(second.IsCompleted);
        remote.Found("9304003", juice);
        remote.Found("9304002", cola);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal("pos.status.added", result.StatusKey));
        Assert.Equal(2, cart.Lines.Count);
        Assert.Equal(8m, cart.TotalAmount);
    }

    [Fact]
    public async Task Cart_cleared_while_lookup_pending_discards_remote_item_but_keeps_local_cache()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(index, cart, remote.LookupAsync);
        var existing = CreateItem("SKU-C4-04", "Existing Tea", "9304004", 2m);
        var remoteItem = CreateItem("SKU-C4-05", "Remote Water", "9304005", 1.5m);
        cart.AddItem(existing);

        var scan = service.ProcessScanAsync(Session, "9304005", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304005");
        service.ClearCart();
        remote.Found("9304005", remoteItem);
        var result = await scan;

        // 清空购物车后才回来的补查结果不得落到清空后的购物车（下一单）里。
        Assert.Equal("pos.status.remoteLookupDiscarded", result.StatusKey);
        Assert.False(result.ClearScanText);
        Assert.Empty(cart.Lines);

        // 商品本身仍写入本地索引：收银员按提示重扫时会本地命中，不必再等一轮补查。
        Assert.Same(remoteItem, Assert.Single(index.FindExactMatches("S001", "9304005")));
        var rescan = await service.ProcessScanAsync(Session, "9304005", preferExactLookup: true, source: "raw");
        Assert.Equal("pos.status.added", rescan.StatusKey);
        Assert.Equal(1m, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public async Task Payment_started_while_lookup_pending_discards_remote_item()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var existing = CreateItem("SKU-C4-06", "Existing Tea", "9304006", 2m);
        var remoteItem = CreateItem("SKU-C4-07", "Remote Water", "9304007", 1.5m);
        cart.AddItem(existing);

        var scan = service.ProcessScanAsync(Session, "9304007", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304007");
        var guard = service.GuardPayment();
        remote.Found("9304007", remoteItem);
        var result = await scan;

        // 收款已开始：付款页看到的金额不能再被迟到的补查结果改动。
        Assert.True(guard.PaymentAllowed);
        Assert.Equal("pos.status.remoteLookupDiscarded", result.StatusKey);
        var line = Assert.Single(cart.Lines);
        Assert.Equal("Existing Tea", line.DisplayName);
        Assert.Equal(2m, cart.TotalAmount);
    }

    [Fact]
    public async Task Payment_guard_rejection_does_not_discard_pending_lookup()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var zeroPrice = CreateItem("SKU-C4-08", "Zero Price Item", "9304008", 0m);
        var remoteItem = CreateItem("SKU-C4-09", "Remote Water", "9304009", 1.5m);
        cart.AddItem(zeroPrice);

        var scan = service.ProcessScanAsync(Session, "9304009", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304009");
        var guard = service.GuardPayment();
        remote.Found("9304009", remoteItem);
        var result = await scan;

        // 收款被拦下（零价商品）时并没有真正进入收款，收银员仍停留在收银页，补查结果照常加购。
        Assert.False(guard.PaymentAllowed);
        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.Equal(2, cart.Lines.Count);
    }

    [Fact]
    public async Task Same_code_rescanned_after_cart_clear_is_not_swallowed_by_the_stale_scan()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var remoteItem = CreateItem("SKU-C4-10", "Remote Cola", "9304010", 3.25m);

        var stale = service.ProcessScanAsync(Session, "9304010", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304010");
        service.ClearCart();
        var current = service.ProcessScanAsync(Session, "9304010", preferExactLookup: true, source: "raw");

        // 去重按「同码 + 同世代」判断：清空后重扫属于新一单，必须能拿到结果，同时共享同一次远程请求。
        Assert.False(current.IsCompleted);
        remote.Found("9304010", remoteItem);
        var results = await Task.WhenAll(stale, current);

        Assert.Equal("pos.status.remoteLookupDiscarded", results[0].StatusKey);
        Assert.Equal("pos.status.added", results[1].StatusKey);
        Assert.Equal(1m, Assert.Single(cart.Lines).Quantity);
        Assert.Equal(1, remote.Calls("9304010"));
    }

    [Fact]
    public async Task Lookup_timeout_keeps_no_match_result_and_releases_the_code_for_a_new_scan()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var timeProvider = new FakeTimeProvider();
        var service = new PosTerminalWorkflowService(
            new LocalSellableItemIndex(),
            cart,
            remote.LookupAsync,
            timeProvider: timeProvider);
        var remoteItem = CreateItem("SKU-C4-11", "Remote Cola", "9304011", 3.25m);

        var first = service.ProcessScanAsync(Session, "9304011", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304011");
        timeProvider.Advance(PosTerminalWorkflowService.RemoteLookupTimeout);
        var timedOut = await first;

        // 超时沿用现有行为：回到「本地未命中」，购物车不变。
        Assert.Equal("pos.status.noLocalMatch", timedOut.StatusKey);
        Assert.Empty(cart.Lines);

        // 超时后「正在查询」登记必须已释放：同码再扫重新发起补查，而不是被当成仍在查询。
        var again = service.ProcessScanAsync(Session, "9304011", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304011", expectedCalls: 2);
        Assert.False(again.IsCompleted);
        remote.Found("9304011", remoteItem);
        Assert.Equal("pos.status.added", (await again).StatusKey);
        Assert.Equal(1m, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public async Task Remote_failure_releases_the_code_for_a_new_scan()
    {
        var cart = new PosCartService();
        var calls = 0;
        var remoteItem = CreateItem("SKU-C4-12", "Remote Cola", "9304012", 3.25m);
        var service = new PosTerminalWorkflowService(
            new LocalSellableItemIndex(),
            cart,
            (storeCode, lookupCode, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("remote failed");
                }

                return Task.FromResult(new RemoteLookupRefreshResult(storeCode, lookupCode, Found: true, remoteItem, DeletedCount: 0));
            });

        var failed = await service.ProcessScanAsync(Session, "9304012", preferExactLookup: true, source: "raw");
        var retried = await service.ProcessScanAsync(Session, "9304012", preferExactLookup: true, source: "raw");

        Assert.Equal("pos.status.noLocalMatch", failed.StatusKey);
        Assert.Equal("pos.status.added", retried.StatusKey);
        Assert.Equal(1m, Assert.Single(cart.Lines).Quantity);
    }

    [Fact]
    public async Task Rescan_after_remote_item_was_added_hits_local_index_and_increments_quantity()
    {
        var cart = new PosCartService();
        var remote = new ControlledRemoteLookup();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart, remote.LookupAsync);
        var remoteItem = CreateItem("SKU-C4-13", "Remote Cola", "9304013", 3.25m);

        var first = service.ProcessScanAsync(Session, "9304013", preferExactLookup: true, source: "raw");
        await remote.WaitForCallAsync("9304013");
        remote.Found("9304013", remoteItem);
        await first;
        var second = await service.ProcessScanAsync(Session, "9304013", preferExactLookup: true, source: "raw");

        // 补查期间的重复扫码被吸收；补查完成后正常的重复扫码仍然是本地命中并累加数量。
        Assert.Equal("pos.status.added", second.StatusKey);
        Assert.Equal(2m, Assert.Single(cart.Lines).Quantity);
    }
}
