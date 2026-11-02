using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银端运行中的目录自动同步节奏：只在空闲时开始、恢复联网补一次、失败逐步退避、白天推迟全量留到夜间。
/// 时间按门店本地 = UTC 处理，白天取 10:00，夜间静默窗口取 23:00。
/// </summary>
public sealed class CatalogAutoSyncSchedulerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset Daytime = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Night = new(2026, 11, 2, 23, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, 30, 3)]
    [InlineData("", 30, 3)]
    [InlineData("abc", 30, 3)]
    [InlineData("60", 60, 3)]
    [InlineData("15", 15, 1.5)]
    [InlineData("3", 10, 1)]
    public void Options_read_interval_from_configuration_with_floor_and_capped_jitter(
        string? configured,
        int expectedIntervalMinutes,
        double expectedMaxJitterMinutes)
    {
        var options = CatalogAutoSyncOptions.FromConfiguration(CreateConfiguration(configured));

        Assert.True(options.IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(expectedIntervalMinutes), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(expectedMaxJitterMinutes), options.MaxJitter);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Options_zero_or_negative_interval_disables_auto_sync(string configured)
    {
        var options = CatalogAutoSyncOptions.FromConfiguration(CreateConfiguration(configured));

        Assert.False(options.IsEnabled);
        var (scheduler, clock) = CreateScheduler(Daytime, options);
        scheduler.Arm(initialSyncStarted: false);
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
    }

    [Fact]
    public void Nothing_starts_before_the_startup_sync_arms_the_scheduler()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
    }

    [Fact]
    public void After_an_online_startup_sync_the_next_run_waits_a_full_interval_from_its_success()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(TimeSpan.FromMinutes(10));
        scheduler.RecordSucceeded();

        clock.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));

        clock.Advance(TimeSpan.FromSeconds(1));
        var start = scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false);
        Assert.Equal(new CatalogAutoSyncStart("interval", AllowFullDownload: false), start);
    }

    [Fact]
    public void Never_starts_while_busy_offline_or_another_sync_is_running_and_starts_on_the_first_idle_tick()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);

        // 中文注释：交易中 / 付款中 / 日结等非收银主页、离线、手动同步进行中都不开始，到期状态一直保留。
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: false, isSyncActive: false));
        Assert.Null(scheduler.TryBegin(isOnline: false, isIdle: true, isSyncActive: false));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: true));

        clock.Advance(TimeSpan.FromMinutes(7));
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
    }

    [Fact]
    public void Only_one_auto_sync_runs_at_a_time()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));

        scheduler.Complete(CatalogAutoSyncOutcome.Succeeded);
        clock.Advance(Interval);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
    }

    [Fact]
    public void Startup_while_offline_catches_up_as_soon_as_the_terminal_is_online_and_idle()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: false);

        Assert.Null(scheduler.TryBegin(isOnline: false, isIdle: true, isSyncActive: false));
        clock.Advance(TimeSpan.FromMinutes(1));

        var start = scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false);
        Assert.Equal("startup-offline", start?.Reason);
    }

    [Fact]
    public void Reconnecting_after_an_outage_syncs_once_but_not_more_often_than_the_minimum_gap()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        scheduler.RecordSucceeded();
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(scheduler.TryBegin(isOnline: false, isIdle: true, isSyncActive: false));
        clock.Advance(TimeSpan.FromMinutes(6));
        var start = scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false);
        Assert.Equal("reconnected", start?.Reason);
        scheduler.Complete(CatalogAutoSyncOutcome.Succeeded);

        // 中文注释：网络抖动：两分钟后又断又连，仍要等满 5 分钟最小间隔才补。
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(scheduler.TryBegin(isOnline: false, isIdle: true, isSyncActive: false));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("reconnected", scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false)?.Reason);
    }

    [Fact]
    public void Failures_back_off_5_10_20_then_30_minutes_and_success_restores_the_normal_interval()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);

        foreach (var expectedMinutes in new[] { 5, 10, 20, 30, 30 })
        {
            Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
            scheduler.Complete(CatalogAutoSyncOutcome.Failed);

            clock.Advance(TimeSpan.FromMinutes(expectedMinutes) - TimeSpan.FromSeconds(1));
            Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var retry = scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false);
        Assert.Equal("retry", retry?.Reason);
        scheduler.Complete(CatalogAutoSyncOutcome.Succeeded);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        clock.Advance(Interval - TimeSpan.FromMinutes(5));
        Assert.Equal("interval", scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false)?.Reason);
    }

    [Fact]
    public void Reconnecting_during_a_failure_backoff_does_not_reset_the_backoff()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.Failed);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.Failed);

        // 中文注释：第二次失败要等 10 分钟；坏网络下第 6 分钟断线重连，不能提前到 5 分钟最小间隔就重试。
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(scheduler.TryBegin(isOnline: false, isIdle: true, isSyncActive: false));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("retry", scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false)?.Reason);
    }

    [Fact]
    public void Manual_or_startup_failures_use_the_same_backoff()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        scheduler.RecordFailed();

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("retry", scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false)?.Reason);
    }

    [Fact]
    public void Daytime_auto_sync_never_allows_a_full_download_but_quiet_hours_do()
    {
        var (dayScheduler, dayClock) = CreateScheduler(Daytime);
        dayScheduler.Arm(initialSyncStarted: true);
        dayClock.Advance(Interval);
        Assert.False(dayScheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false)!.AllowFullDownload);

        var (nightScheduler, nightClock) = CreateScheduler(Night);
        nightScheduler.Arm(initialSyncStarted: true);
        nightClock.Advance(Interval);
        Assert.True(nightScheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false)!.AllowFullDownload);
    }

    [Fact]
    public void A_deferred_full_download_pauses_daytime_checks_until_quiet_hours_then_allows_full()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.FullDownloadDeferred);
        Assert.True(scheduler.IsFullDownloadPending);

        // 中文注释：白天再问也是全量，10:30 → 21:59 一次都不开始。
        for (var time = clock.GetUtcNow(); time < Night.AddHours(-1); time = clock.GetUtcNow())
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            if (BackgroundPollingSchedule.IsQuietHours(clock.GetLocalNow()))
            {
                break;
            }

            Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        }

        clock.SetUtcNow(Night);
        var nightStart = scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false);
        Assert.Equal(new CatalogAutoSyncStart("deferred-full", AllowFullDownload: true), nightStart);
        scheduler.Complete(CatalogAutoSyncOutcome.Succeeded);
        Assert.False(scheduler.IsFullDownloadPending);
    }

    [Fact]
    public void A_deferred_full_download_is_not_a_failure_and_does_not_lengthen_the_backoff()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.Failed);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.FullDownloadDeferred);

        // 中文注释：推迟全量把失败计数清零：夜里那次全量再失败，按第一次失败的 5 分钟重试，而不是接着拉长到 10 分钟。
        clock.SetUtcNow(Night);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.Failed);

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(
            new CatalogAutoSyncStart("deferred-full", AllowFullDownload: true),
            scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
    }

    [Fact]
    public void Manual_sync_success_clears_a_deferred_full_download_and_resumes_daytime_checks()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.FullDownloadDeferred);

        clock.Advance(TimeSpan.FromMinutes(40));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.RecordSucceeded();

        clock.Advance(Interval);
        Assert.Equal(new CatalogAutoSyncStart("interval", AllowFullDownload: false), scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
    }

    [Fact]
    public void A_canceled_auto_sync_is_retried_after_the_minimum_gap_without_counting_as_a_failure()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.Canceled);

        clock.Advance(CatalogAutoSyncScheduler.MinimumAttemptGap - TimeSpan.FromSeconds(1));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
        scheduler.Complete(CatalogAutoSyncOutcome.Failed);

        // 中文注释：取消不计入失败次数，这次失败仍是第一次，5 分钟后重试。
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("retry", scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false)?.Reason);
    }

    [Fact]
    public void Disarm_stops_auto_sync_until_the_next_startup_sync_arms_it_again()
    {
        var (scheduler, clock) = CreateScheduler(Daytime);
        scheduler.Arm(initialSyncStarted: true);
        scheduler.Disarm();
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));

        scheduler.Arm(initialSyncStarted: true);
        clock.Advance(Interval);
        Assert.NotNull(scheduler.TryBegin(isOnline: true, isIdle: true, isSyncActive: false));
    }

    [Fact]
    public void Jitter_delays_the_next_run_by_at_most_the_configured_maximum()
    {
        var options = CatalogAutoSyncOptions.Create(Interval);
        var (scheduler, clock) = CreateScheduler(Daytime, options);
        scheduler.Arm(initialSyncStarted: true);

        var delay = scheduler.NextDueUtc - clock.GetUtcNow();
        Assert.InRange(delay, Interval, Interval + options.MaxJitter);
    }

    private static (CatalogAutoSyncScheduler Scheduler, FakeTimeProvider Clock) CreateScheduler(
        DateTimeOffset start,
        CatalogAutoSyncOptions? options = null)
    {
        var clock = new FakeTimeProvider(start);
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var scheduler = new CatalogAutoSyncScheduler(options ?? new CatalogAutoSyncOptions(Interval, TimeSpan.Zero), clock);
        return (scheduler, clock);
    }

    private static IConfiguration CreateConfiguration(string? intervalMinutes)
    {
        var values = new Dictionary<string, string?>();
        if (intervalMinutes is not null)
        {
            values[CatalogAutoSyncOptions.IntervalMinutesConfigurationKey] = intervalMinutes;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
