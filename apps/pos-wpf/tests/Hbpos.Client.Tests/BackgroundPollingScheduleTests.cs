using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class BackgroundPollingScheduleTests
{
    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("2026-10-22T12:00:00+11:00", false)] // 白天
    [InlineData("2026-10-22T21:59:59+11:00", false)] // 静默窗口开始前一秒
    [InlineData("2026-10-22T22:00:00+11:00", true)] // 静默窗口开始（含）
    [InlineData("2026-10-22T23:30:00+11:00", true)]
    [InlineData("2026-10-22T00:00:00+11:00", true)] // 跨午夜
    [InlineData("2026-10-22T05:59:59+11:00", true)] // 静默窗口结束前一秒
    [InlineData("2026-10-22T06:00:00+11:00", false)] // 静默窗口结束（不含）
    public void Quiet_hours_are_judged_by_the_device_local_time(string localNow, bool expected)
    {
        Assert.Equal(expected, BackgroundPollingSchedule.IsQuietHours(DateTimeOffset.Parse(localNow)));
    }

    [Fact]
    public void Quiet_hours_use_the_local_clock_component_not_utc()
    {
        // 同一个时刻：悉尼本地 23:00（UTC 12:00）属于夜间，珀斯本地 20:00 不属于。
        Assert.True(BackgroundPollingSchedule.IsQuietHours(DateTimeOffset.Parse("2026-10-22T23:00:00+11:00")));
        Assert.False(BackgroundPollingSchedule.IsQuietHours(DateTimeOffset.Parse("2026-10-22T20:00:00+08:00")));
    }

    [Fact]
    public void The_interval_slows_down_at_night_and_returns_to_normal_in_the_day()
    {
        Assert.Equal(
            BackgroundPollingSchedule.QuietInterval,
            BackgroundPollingSchedule.GetInterval(DateTimeOffset.Parse("2026-10-22T03:00:00+11:00"), Normal));
        Assert.Equal(
            Normal,
            BackgroundPollingSchedule.GetInterval(DateTimeOffset.Parse("2026-10-22T10:00:00+11:00"), Normal));
    }

    [Fact]
    public void The_quiet_interval_never_makes_a_slower_poll_faster()
    {
        var alreadySlow = TimeSpan.FromMinutes(10);

        Assert.Equal(
            alreadySlow,
            BackgroundPollingSchedule.GetInterval(DateTimeOffset.Parse("2026-10-22T03:00:00+11:00"), alreadySlow));
    }
}
