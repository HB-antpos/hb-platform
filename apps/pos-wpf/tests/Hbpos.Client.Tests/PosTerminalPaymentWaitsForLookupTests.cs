using System.Collections.Concurrent;
using System.Diagnostics;
using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;
using static Hbpos.Client.Tests.PosTerminalRemoteLookupTestSupport;

namespace Hbpos.Client.Tests;

/// <summary>
/// C4：补查期间点「收款」——先等在途补查结束（最多 2 秒，用 await，不阻塞 UI 线程）再进付款页，
/// 这样刚扫的商品也算进这一单。本文件只使用该行为实现前就存在的公开 API，便于在旧代码上跑出失败对照。
/// </summary>
[Collection(CultureSensitiveTestCollection.Name)]
public sealed class PosTerminalPaymentWaitsForLookupTests
{
    [Fact]
    public async Task Open_payment_while_lookup_pending_waits_for_result_and_includes_the_scanned_item()
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-P01", "Existing Tea", "9306101", 2m));

        harness.Scan("9306102");
        await harness.Remote.WaitForCallAsync("9306102");
        var payment = harness.OpenPaymentAsync();

        // 补查还没回来：收款先等着，不能带着缺了刚扫商品的购物车进付款页。
        Assert.False(payment.IsCompleted, "补查进行中点收款应当先等补查结束。");
        Assert.Equal(0, harness.PaymentOpenCount);

        harness.Remote.Found("9306102", CreateItem("SKU-C4-P02", "Remote Water", "9306102", 1.5m));
        await payment.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        // 打开付款页那一刻，刚扫的商品已经在购物车里。
        Assert.Equal([2], harness.LinesAtPayment);
        Assert.Equal(3.5m, harness.Cart.TotalAmount);
    }

    [Fact]
    public async Task Open_payment_after_lookup_timeout_proceeds_with_the_cart_unchanged()
    {
        var timeProvider = new FakeTimeProvider();
        using var harness = new TerminalHarness(timeProvider);
        harness.Cart.AddItem(CreateItem("SKU-C4-P03", "Existing Tea", "9306103", 2m));

        harness.Scan("9306104");
        await harness.Remote.WaitForCallAsync("9306104");
        var payment = harness.OpenPaymentAsync();
        Assert.False(payment.IsCompleted, "补查进行中点收款应当先等补查结束。");

        // 补查超时（沿用现有的 2 秒超时）：回到「未命中」，收款照常进行，购物车不变。
        timeProvider.Advance(PosTerminalWorkflowService.RemoteLookupTimeout);
        await payment.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal([1], harness.LinesAtPayment);
        Assert.Equal("Existing Tea", Assert.Single(harness.Cart.Lines).DisplayName);
    }

    [Fact]
    public async Task Cart_cleared_while_payment_waits_does_not_open_payment()
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-P04", "Existing Tea", "9306105", 2m));

        harness.Scan("9306106");
        await harness.Remote.WaitForCallAsync("9306106");
        var payment = harness.OpenPaymentAsync();
        Assert.False(payment.IsCompleted, "补查进行中点收款应当先等补查结束。");

        await harness.ClearCartAsync();
        harness.Remote.Found("9306106", CreateItem("SKU-C4-P05", "Remote Water", "9306106", 1.5m));
        await payment.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        // 等待期间购物车被清空：沿用「购物车为空不可收款」的规则，不进付款页；迟到的补查结果也被丢弃。
        Assert.Equal(0, harness.PaymentOpenCount);
        Assert.Empty(harness.Cart.Lines);
        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pressing_pay_again_while_waiting_opens_payment_only_once()
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-P06", "Existing Tea", "9306107", 2m));

        harness.Scan("9306108");
        await harness.Remote.WaitForCallAsync("9306108");
        var first = harness.OpenPaymentAsync();
        var second = harness.OpenPaymentAsync();
        Assert.False(first.IsCompleted, "补查进行中点收款应当先等补查结束。");

        harness.Remote.Found("9306108", CreateItem("SKU-C4-P07", "Remote Water", "9306108", 1.5m));
        await Task.WhenAll(first, second).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        // 等待期间重复点收款不能把付款页打开两次。
        Assert.Equal([2], harness.LinesAtPayment);
    }

    [Fact]
    public async Task Open_payment_waits_only_for_lookups_started_before_the_click()
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-P08", "Existing Tea", "9306109", 2m));

        harness.Scan("9306110");
        await harness.Remote.WaitForCallAsync("9306110");
        var payment = harness.OpenPaymentAsync();
        Assert.False(payment.IsCompleted, "补查进行中点收款应当先等补查结束。");

        // 点收款之后才扫的商品不在等待范围内：第一件回来就进付款页，不会被后面的扫码无限拖住。
        harness.Scan("9306111");
        await harness.Remote.WaitForCallAsync("9306111");
        harness.Remote.Found("9306110", CreateItem("SKU-C4-P09", "Remote Water", "9306110", 1.5m));
        await payment.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.Equal([2], harness.LinesAtPayment);

        // 付款页已打开，之后才回来的第二件按世代规则丢弃，不会改动付款页上的购物车。
        harness.Remote.Found("9306111", CreateItem("SKU-C4-P10", "Late Juice", "9306111", 5m));
        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));
        Assert.Equal(2, harness.Cart.Lines.Count);
        Assert.Equal(3.5m, harness.Cart.TotalAmount);
    }

    [Fact]
    public async Task Open_payment_without_pending_lookup_does_not_wait()
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-P11", "Existing Tea", "9306112", 2m));

        var payment = harness.OpenPaymentAsync();

        Assert.True(payment.IsCompleted);
        await payment;
        Assert.Equal([1], harness.LinesAtPayment);
    }

    [Fact]
    public async Task Open_payment_stays_unavailable_for_an_empty_cart_even_while_a_lookup_is_pending()
    {
        using var harness = new TerminalHarness();

        harness.Scan("9306113");
        await harness.Remote.WaitForCallAsync("9306113");

        // 现有规则不变：购物车为空时收款不可用；补查把商品加进来之后才可用。
        Assert.False(harness.ViewModel.OpenPaymentCommand.CanExecute(null));
        harness.Remote.Found("9306113", CreateItem("SKU-C4-P12", "Remote Water", "9306113", 1.5m));
        await WaitUntilAsync(() => harness.Cart.Lines.Count == 1);
        Assert.True(harness.ViewModel.OpenPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Waiting_for_lookup_does_not_block_the_ui_thread_and_resumes_payment_on_it()
    {
        var outcome = await Task.Factory
            .StartNew(RunOnSimulatedUiThread, TaskCreationOptions.LongRunning)
            .WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        // 点收款立刻返回（没有同步等待），等待期间消息循环仍能处理别的工作，补查回来后在 UI 线程上继续并进付款页。
        Assert.True(outcome.ReturnedWithoutBlocking);
        Assert.True(outcome.PumpAliveWhileWaiting);
        Assert.Equal(0, outcome.PaymentCountWhileWaiting);
        Assert.Equal([2], outcome.LinesAtPayment);
        Assert.Equal([outcome.UiThreadId], outcome.PaymentThreadIds);
    }

    private static UiThreadOutcome RunOnSimulatedUiThread()
    {
        var pump = new PumpSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            using var harness = new TerminalHarness();
            harness.Cart.AddItem(CreateItem("SKU-C4-P13", "Existing Tea", "9306114", 2m));
            harness.Scan("9306115");
            pump.RunUntil(() => harness.Remote.Calls("9306115") == 1);

            var payment = harness.OpenPaymentAsync();
            var returnedWithoutBlocking = !payment.IsCompleted;

            // 等待期间在「UI 线程」上投递一项别的工作：消息循环没被收款阻塞就会立刻处理它。
            var marker = false;
            pump.Post(_ => marker = true, null);
            pump.RunUntil(() => marker);
            var paymentCountWhileWaiting = harness.PaymentOpenCount;

            _ = Task.Run(() => harness.Remote.Found("9306115", CreateItem("SKU-C4-P14", "Remote Water", "9306115", 1.5m)));
            pump.RunUntil(() => payment.IsCompleted);
            payment.GetAwaiter().GetResult();

            return new UiThreadOutcome(
                returnedWithoutBlocking,
                marker,
                paymentCountWhileWaiting,
                harness.LinesAtPayment,
                harness.PaymentThreadIds,
                Environment.CurrentManagedThreadId);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    private sealed record UiThreadOutcome(
        bool ReturnedWithoutBlocking,
        bool PumpAliveWhileWaiting,
        int PaymentCountWhileWaiting,
        IReadOnlyList<int> LinesAtPayment,
        IReadOnlyList<int> PaymentThreadIds,
        int UiThreadId);

    /// <summary>最小化的单线程消息循环，模拟 WPF Dispatcher：continuation 必须通过它回到「UI 线程」。</summary>
    private sealed class PumpSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public void RunUntil(Func<bool> condition)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.Elapsed > AsyncTestWaitSupport.DefaultTimeout)
                {
                    throw new TimeoutException("模拟 UI 线程等待条件超时。");
                }

                if (_queue.TryTake(out var item, 20))
                {
                    item.Callback(item.State);
                }
            }
        }
    }
}
