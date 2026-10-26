using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;
using Microsoft.Extensions.Time.Testing;

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
    public void The_qr_panel_refresh_is_five_minutes_when_healthy_and_fifteen_seconds_otherwise()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), BackgroundPollingSchedule.GetQrPanelRefreshInterval(healthy: true));
        Assert.Equal(TimeSpan.FromSeconds(15), BackgroundPollingSchedule.GetQrPanelRefreshInterval(healthy: false));
    }

    [Fact]
    public void The_quiet_interval_never_makes_a_slower_poll_faster()
    {
        var alreadySlow = TimeSpan.FromMinutes(10);

        Assert.Equal(
            alreadySlow,
            BackgroundPollingSchedule.GetInterval(DateTimeOffset.Parse("2026-10-22T03:00:00+11:00"), alreadySlow));
    }

    // ───────────── GetNextPollDelay：挂单角标 / 收银员会话刷新用的「每次重算」延迟 ─────────────

    private static readonly TimeSpan Sixty = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData("2026-10-22T10:00:00+11:00", 60)] // 白天：原间隔
    [InlineData("2026-10-22T21:59:00+11:00", 60)] // 窗口开始前：仍是原间隔
    [InlineData("2026-10-22T21:59:59+11:00", 60)]
    [InlineData("2026-10-22T22:00:00+11:00", 300)] // 窗口开始（含）：5 分钟
    [InlineData("2026-10-22T23:30:00+11:00", 300)]
    [InlineData("2026-10-23T00:00:00+11:00", 300)] // 跨午夜
    [InlineData("2026-10-23T03:00:00+11:00", 300)]
    [InlineData("2026-10-23T05:54:00+11:00", 300)] // 距 06:00 还有 6 分钟：仍是整 5 分钟
    [InlineData("2026-10-23T05:55:00+11:00", 300)] // 刚好 5 分钟：落在 06:00
    [InlineData("2026-10-23T06:00:00+11:00", 60)] // 窗口结束（不含）：恢复原间隔
    [InlineData("2026-10-23T06:00:01+11:00", 60)]
    public void Next_poll_delay_is_sixty_seconds_by_day_and_five_minutes_at_night(string localNow, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            BackgroundPollingSchedule.GetNextPollDelay(DateTimeOffset.Parse(localNow), Sixty));
    }

    [Theory]
    [InlineData("2026-10-23T05:55:01+11:00", 299)] // 距 06:00 不足 5 分钟：截到 06:00，窗口一结束就补一次
    [InlineData("2026-10-23T05:57:00+11:00", 180)]
    [InlineData("2026-10-23T05:59:30+11:00", 30)]
    [InlineData("2026-10-23T05:59:59+11:00", 1)]
    [InlineData("2026-10-23T05:59:59.9+11:00", 1)] // 下限 1 秒，避免窗口末尾空转
    public void Next_poll_delay_is_cut_to_the_quiet_window_end_so_the_morning_poll_is_not_late(
        string localNow,
        double expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            BackgroundPollingSchedule.GetNextPollDelay(DateTimeOffset.Parse(localNow), Sixty));
    }

    [Fact]
    public void Next_poll_delay_never_makes_a_slower_poll_faster_except_to_land_on_the_window_end()
    {
        var tenMinutes = TimeSpan.FromMinutes(10);

        // 原间隔本来就比夜间间隔慢：夜里中途保持 10 分钟，不会被「加速」到 5 分钟。
        Assert.Equal(
            tenMinutes,
            BackgroundPollingSchedule.GetNextPollDelay(DateTimeOffset.Parse("2026-10-23T01:00:00+11:00"), tenMinutes));
    }

    // ───────────── 收银员会话刷新：每次 tick 重算间隔 ─────────────

    [Theory]
    [InlineData("2026-10-22T10:00:00+11:00", 60)]
    [InlineData("2026-10-22T23:00:00+11:00", 300)]
    [InlineData("2026-10-23T06:00:00+11:00", 60)]
    public void Cashier_session_refresh_delay_follows_the_quiet_window(string localNow, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            CashierSessionRefreshHostedService.GetNextRefreshDelay(DateTimeOffset.Parse(localNow)));
    }

    [Fact]
    public async Task Cashier_session_refresh_runs_every_sixty_seconds_by_day()
    {
        await using var harness = await RefreshHarness.StartAsync("2026-10-22T10:00:00Z");

        harness.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, harness.Api.CallCount);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.WaitForCallsAsync(1);

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.WaitForCallsAsync(2);
    }

    [Fact]
    public async Task Cashier_session_refresh_slows_to_five_minutes_at_night()
    {
        await using var harness = await RefreshHarness.StartAsync("2026-10-23T03:00:00Z");

        harness.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, harness.Api.CallCount);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.WaitForCallsAsync(1);

        // 下一轮仍是 5 分钟，不是 60 秒。
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, harness.Api.CallCount);
        harness.Clock.Advance(TimeSpan.FromMinutes(4));
        await harness.WaitForCallsAsync(2);
    }

    [Fact]
    public async Task Cashier_session_refresh_polls_right_at_quiet_window_end_then_returns_to_sixty_seconds()
    {
        // 05:57 启动：夜间间隔会落到 06:02，必须被截到 06:00 整，早上开门前就刷新一次。
        await using var harness = await RefreshHarness.StartAsync("2026-10-23T05:57:00Z");

        harness.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, harness.Api.CallCount);
        harness.Clock.Advance(TimeSpan.FromSeconds(1)); // 06:00:00
        await harness.WaitForCallsAsync(1);

        // 窗口已结束：恢复 60 秒。
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.WaitForCallsAsync(2);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.WaitForCallsAsync(3);
    }

    [Fact]
    public async Task Cashier_session_refresh_switches_to_five_minutes_after_entering_the_quiet_window()
    {
        // 21:59:30 启动：首个间隔仍按白天算，22:00:30 触发后改成 5 分钟。
        await using var harness = await RefreshHarness.StartAsync("2026-10-22T21:59:30Z");

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.WaitForCallsAsync(1);

        harness.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal(1, harness.Api.CallCount);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.WaitForCallsAsync(2);
    }

    /// <summary>
    /// 启动真实的 <see cref="CashierSessionRefreshHostedService"/>，用 FakeTimeProvider（本地时区 = UTC）控制「设备本地时间」，
    /// 用只计数的假刷新接口观察实际调用节奏。会话上下文里放一个已登录收银员，否则 RefreshOnceAsync 不会发请求。
    /// </summary>
    private sealed class RefreshHarness : IAsyncDisposable
    {
        private readonly CashierSessionRefreshHostedService _service;

        private RefreshHarness(FakeTimeProvider clock, CountingRefreshApiClient api, CashierSessionRefreshHostedService service)
        {
            Clock = clock;
            Api = api;
            _service = service;
        }

        public FakeTimeProvider Clock { get; }

        public CountingRefreshApiClient Api { get; }

        public static async Task<RefreshHarness> StartAsync(string startUtcAsLocal)
        {
            // FakeTimeProvider 的本地时区默认就是 UTC，所以这里的 UTC 时刻即设备本地时间。
            var clock = new FakeTimeProvider(DateTimeOffset.Parse(startUtcAsLocal));
            var api = new CountingRefreshApiClient();
            var context = new CashierSessionContext();
            context.SetCurrent(new CashierSessionDto(
                "C001", "U-1", "Alice", "S001", "POS-01", [], [], ["S001"], false, false, false));
            var refresh = new CashierSessionRefreshService(api, context, new NoopCacheUpdater());
            var service = new CashierSessionRefreshHostedService(refresh, clock);
            await service.StartAsync(CancellationToken.None);
            return new RefreshHarness(clock, api, service);
        }

        public Task WaitForCallsAsync(int expected) => WaitUntilAsync(() => Api.CallCount >= expected);

        public async ValueTask DisposeAsync() => await _service.StopAsync(CancellationToken.None);
    }

    private sealed class CountingRefreshApiClient : ICashierSessionRefreshApiClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CashierSessionRefreshAttempt> RefreshAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            // 返回「服务不可用」：会话保持不变，不触发缓存写入或踢下线。
            return Task.FromResult(CashierSessionRefreshAttempt.ApiUnavailable());
        }
    }

    private sealed class NoopCacheUpdater : ICashierSessionCacheUpdater
    {
        public Task UpdateCachedSessionAsync(CashierSessionDto session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveCachedSessionAsync(CashierSessionDto session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
