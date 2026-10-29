using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>日结后自动登出的判定：到时登出；换人、又开始收银、付款进行中则作废；日结页忙时顺延。</summary>
public sealed class DailyCloseAutoLogoutTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 11, 0, 0, TimeSpan.Zero);
    private const string Cashier = "C001|guid-1";

    [Fact]
    public void Nothing_pending_by_default()
    {
        var tracker = new DailyCloseAutoLogoutTracker();

        Assert.False(tracker.IsPending);
        Assert.Equal(DailyCloseAutoLogoutDecision.None, Evaluate(tracker, Now));
    }

    [Fact]
    public void Logs_out_once_the_delay_has_passed()
    {
        var tracker = new DailyCloseAutoLogoutTracker();
        var restartAt = Now.AddHours(16);

        Assert.Equal(TimeSpan.FromSeconds(30), tracker.Schedule(Cashier, 0, Now, restartAt));
        Assert.Equal(DailyCloseAutoLogoutDecision.Wait, Evaluate(tracker, Now.AddSeconds(29)));
        Assert.Equal(DailyCloseAutoLogoutDecision.Logout, Evaluate(tracker, Now.AddSeconds(30)));

        Assert.Equal(restartAt, tracker.RestartAt);
        Assert.False(tracker.IsPending);
        Assert.Equal(DailyCloseAutoLogoutDecision.None, Evaluate(tracker, Now.AddSeconds(31)));
    }

    [Fact]
    public void Cancelled_when_another_cashier_signed_in_or_nobody_is_signed_in()
    {
        var other = new DailyCloseAutoLogoutTracker();
        other.Schedule(Cashier, 0, Now, null);
        Assert.Equal(DailyCloseAutoLogoutDecision.Cancelled, Evaluate(other, Now.AddSeconds(5), cashier: "C002|guid-2"));

        var none = new DailyCloseAutoLogoutTracker();
        none.Schedule(Cashier, 0, Now, null);
        Assert.Equal(DailyCloseAutoLogoutDecision.Cancelled, Evaluate(none, Now.AddSeconds(5), cashier: null));
        Assert.False(none.IsPending);
    }

    [Fact]
    public void Cancelled_when_the_cashier_resumes_selling()
    {
        var cart = new DailyCloseAutoLogoutTracker();
        cart.Schedule(Cashier, 0, Now, null);
        Assert.Equal(DailyCloseAutoLogoutDecision.Cancelled, Evaluate(cart, Now.AddSeconds(5), cartLines: 1));

        var payment = new DailyCloseAutoLogoutTracker();
        payment.Schedule(Cashier, 0, Now, null);
        Assert.Equal(DailyCloseAutoLogoutDecision.Cancelled, Evaluate(payment, Now.AddSeconds(5), paymentActive: true));
    }

    [Fact]
    public void Cart_lines_left_from_before_the_daily_close_do_not_cancel()
    {
        var tracker = new DailyCloseAutoLogoutTracker();
        tracker.Schedule(Cashier, 2, Now, null);

        Assert.Equal(DailyCloseAutoLogoutDecision.Logout, Evaluate(tracker, Now.AddSeconds(30), cartLines: 2));
    }

    [Fact]
    public void Busy_daily_close_page_postpones_by_a_full_delay()
    {
        var tracker = new DailyCloseAutoLogoutTracker();
        tracker.Schedule(Cashier, 0, Now, null);

        Assert.Equal(DailyCloseAutoLogoutDecision.Wait, Evaluate(tracker, Now.AddSeconds(40), busy: true));
        Assert.Equal(DailyCloseAutoLogoutDecision.Wait, Evaluate(tracker, Now.AddSeconds(60)));
        Assert.Equal(DailyCloseAutoLogoutDecision.Logout, Evaluate(tracker, Now.AddSeconds(70)));
    }

    private static DailyCloseAutoLogoutDecision Evaluate(
        DailyCloseAutoLogoutTracker tracker,
        DateTimeOffset now,
        string? cashier = Cashier,
        int cartLines = 0,
        bool paymentActive = false,
        bool busy = false) =>
        tracker.Evaluate(now, cashier, cartLines, paymentActive, busy);
}
