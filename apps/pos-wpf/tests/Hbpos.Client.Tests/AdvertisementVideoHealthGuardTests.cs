using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class AdvertisementVideoHealthGuardTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(450);

    [Fact]
    public void Healthy_machine_never_suspends_even_with_isolated_slow_opens()
    {
        var guard = new AdvertisementVideoHealthGuard(new FakeTimeProvider());

        // 偶发一次慢（磁盘刚唤醒等）夹在快速打开之间，计数每次被清零，不应触发暂停。
        for (var i = 0; i < 20; i++)
        {
            Assert.False(guard.RecordOpen(Slow));
            Assert.False(guard.RecordOpen(Fast));
        }

        Assert.Equal(VideoPlaybackPermission.Allowed, guard.Check());
    }

    [Fact]
    public void Consecutive_slow_opens_trip_suspension_on_the_limit_exactly()
    {
        var guard = new AdvertisementVideoHealthGuard(new FakeTimeProvider());

        Assert.False(guard.RecordOpen(Slow));
        Assert.False(guard.RecordOpen(Slow));
        Assert.Equal(VideoPlaybackPermission.Allowed, guard.Check());

        Assert.True(guard.RecordOpen(Slow));
        Assert.Equal(VideoPlaybackPermission.Suspended, guard.Check());
    }

    [Fact]
    public void Open_exactly_at_threshold_counts_as_slow_and_just_below_does_not()
    {
        var guard = new AdvertisementVideoHealthGuard(new FakeTimeProvider());
        var threshold = guard.SlowOpenThreshold;

        for (var i = 0; i < AdvertisementVideoHealthGuard.DefaultConsecutiveSlowLimit * 3; i++)
        {
            Assert.False(guard.RecordOpen(threshold - TimeSpan.FromMilliseconds(1)));
        }

        Assert.Equal(VideoPlaybackPermission.Allowed, guard.Check());

        Assert.False(guard.RecordOpen(threshold));
        Assert.False(guard.RecordOpen(threshold));
        Assert.True(guard.RecordOpen(threshold));
    }

    [Fact]
    public void A_fast_open_in_the_middle_resets_the_consecutive_count()
    {
        var guard = new AdvertisementVideoHealthGuard(new FakeTimeProvider());

        Assert.False(guard.RecordOpen(Slow));
        Assert.False(guard.RecordOpen(Slow));
        Assert.False(guard.RecordOpen(Fast));
        Assert.False(guard.RecordOpen(Slow));
        Assert.False(guard.RecordOpen(Slow));

        Assert.Equal(VideoPlaybackPermission.Allowed, guard.Check());
        Assert.True(guard.RecordOpen(Slow));
    }

    [Fact]
    public void Suspension_lasts_for_the_configured_duration_then_reports_resumed_once()
    {
        var clock = new FakeTimeProvider();
        var guard = new AdvertisementVideoHealthGuard(clock);
        TripSuspension(guard);

        clock.Advance(guard.SuspendDuration - TimeSpan.FromSeconds(1));
        Assert.Equal(VideoPlaybackPermission.Suspended, guard.Check());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(VideoPlaybackPermission.Resumed, guard.Check());
        // Resumed 只报告一次，之后就是普通的 Allowed。
        Assert.Equal(VideoPlaybackPermission.Allowed, guard.Check());
        Assert.Equal(VideoPlaybackPermission.Allowed, guard.Check());
    }

    [Fact]
    public void After_resume_the_count_starts_from_zero_so_one_slow_open_does_not_suspend_again()
    {
        var clock = new FakeTimeProvider();
        var guard = new AdvertisementVideoHealthGuard(clock);
        TripSuspension(guard);
        clock.Advance(guard.SuspendDuration);
        Assert.Equal(VideoPlaybackPermission.Resumed, guard.Check());

        Assert.False(guard.RecordOpen(Slow));
        Assert.False(guard.RecordOpen(Slow));
        Assert.Equal(VideoPlaybackPermission.Allowed, guard.Check());
    }

    [Fact]
    public void Still_slow_after_resume_suspends_again_for_a_full_duration()
    {
        var clock = new FakeTimeProvider();
        var guard = new AdvertisementVideoHealthGuard(clock);
        TripSuspension(guard);
        clock.Advance(guard.SuspendDuration);
        Assert.Equal(VideoPlaybackPermission.Resumed, guard.Check());

        TripSuspension(guard);
        clock.Advance(guard.SuspendDuration - TimeSpan.FromSeconds(1));
        Assert.Equal(VideoPlaybackPermission.Suspended, guard.Check());
    }

    [Fact]
    public void Late_open_events_while_suspended_are_ignored_and_do_not_extend_the_suspension()
    {
        var clock = new FakeTimeProvider();
        var guard = new AdvertisementVideoHealthGuard(clock);
        TripSuspension(guard);

        clock.Advance(TimeSpan.FromMinutes(10));
        // 暂停期间迟到的慢打开事件既不重复触发，也不顺延暂停到期时间。
        for (var i = 0; i < 10; i++)
        {
            Assert.False(guard.RecordOpen(Slow));
        }

        clock.Advance(guard.SuspendDuration - TimeSpan.FromMinutes(10));
        Assert.Equal(VideoPlaybackPermission.Resumed, guard.Check());
    }

    [Fact]
    public void Custom_limit_below_one_is_clamped_so_a_single_slow_open_still_needs_a_real_slow_open()
    {
        var guard = new AdvertisementVideoHealthGuard(new FakeTimeProvider(), consecutiveSlowLimit: 0);

        Assert.False(guard.RecordOpen(Fast));
        Assert.Equal(1, guard.ConsecutiveSlowLimit);
        Assert.True(guard.RecordOpen(Slow));
    }

    private static void TripSuspension(AdvertisementVideoHealthGuard guard)
    {
        for (var i = 0; i < guard.ConsecutiveSlowLimit - 1; i++)
        {
            Assert.False(guard.RecordOpen(Slow));
        }

        Assert.True(guard.RecordOpen(Slow));
        Assert.Equal(VideoPlaybackPermission.Suspended, guard.Check());
    }
}
