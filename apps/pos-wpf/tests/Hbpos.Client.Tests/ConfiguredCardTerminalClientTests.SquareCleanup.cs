using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

// 收银员取消刷卡后，「取消 + 关闭结账」两次请求共用的清理上限。
// Hbpos API 在新加坡，门店往返约 0.3 秒；网络一抖，两次请求加起来很容易超过原来的 5 秒，
// 把一次本可确认的取消记成「结果未知」。时间走 FakeTimeProvider，按虚拟时间推进，不靠墙钟硬等。
public sealed partial class ConfiguredCardTerminalClientTests
{
    [Fact]
    public async Task Cleanup_confirms_cancel_when_cancel_and_dismiss_together_take_twelve_seconds()
    {
        // 取消 6 秒 + 关闭 6 秒 = 12 秒：超过原来的 5 秒，但在新的 15 秒内，必须得到明确的「已取消」。
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "IN_PROGRESS")],
            CancelResponseStatus = "CANCEL_REQUESTED",
            CancelDelay = TimeSpan.FromSeconds(6),
            DismissDelay = TimeSpan.FromSeconds(6)
        });
        using var cancellation = new CancellationTokenSource();

        var authorizeTask = harness.AuthorizeAsync(cancellation.Token);
        await WaitUntilAsync(() => harness.Script.CheckoutGets >= 1);
        cancellation.Cancel();
        var result = await harness.AdvanceUntilCompletedAsync(authorizeTask);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("payment.card.squareCanceledSeller", result.StatusKey);
        Assert.Equal(1, harness.Script.Cancels);
        Assert.Equal(1, harness.Script.Dismisses);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Canceled, harness.Repository.FailedStatus);
    }

    [Fact]
    public async Task Cleanup_is_still_bounded_and_falls_back_to_unknown_when_it_exceeds_fifteen_seconds()
    {
        // 取消 10 秒 + 关闭 10 秒 = 20 秒 > 15 秒：必须在上限处截断，不能无限等；
        // 此时无法确认终端已取消，保持「结果未知」交给恢复中心（原有安全语义）。
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "IN_PROGRESS")],
            CancelResponseStatus = "CANCEL_REQUESTED",
            CancelDelay = TimeSpan.FromSeconds(10),
            DismissDelay = TimeSpan.FromSeconds(10)
        // 取消确认窗口走真实时间，期间测试循环仍在推进虚拟时间，会把 Elapsed 撑大；窗口设为零让它立即结束，
        // 这样 Elapsed 才只反映「清理上限」本身。
        }, cancelConfirmWindow: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        var authorizeTask = harness.AuthorizeAsync(cancellation.Token);
        await WaitUntilAsync(() => harness.Script.CheckoutGets >= 1);
        cancellation.Cancel();
        var result = await harness.AdvanceUntilCompletedAsync(authorizeTask);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
        // 虚拟时间只比 15 秒上限多出收尾推进的几步，绝不是 20 秒以上。
        Assert.InRange(harness.Elapsed, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(19));
    }

    [Fact]
    public async Task Cleanup_with_fast_network_finishes_without_waiting()
    {
        // 正常网络（请求立即返回）：清理不受影响，不推进虚拟时间也能得到结果。
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "IN_PROGRESS")],
            CancelResponseStatus = "CANCEL_REQUESTED"
        });
        using var cancellation = new CancellationTokenSource();

        var authorizeTask = harness.AuthorizeAsync(cancellation.Token);
        await WaitUntilAsync(() => harness.Script.CheckoutGets >= 1);
        cancellation.Cancel();
        var result = await authorizeTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal("payment.card.squareCanceledSeller", result.StatusKey);
        Assert.Equal(TimeSpan.Zero, harness.Elapsed);
    }
}
