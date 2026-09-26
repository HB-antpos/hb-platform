using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Configuration;

namespace Hbpos.Client.Tests;

public sealed class AppUpdateBackgroundCheckSchedulerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(null, 60, 10)]
    [InlineData("", 60, 10)]
    [InlineData("abc", 60, 10)]
    [InlineData("30", 30, 6)]
    [InlineData("2", 5, 1)]
    [InlineData("240", 240, 10)]
    public void Options_read_interval_from_configuration_with_floor_and_capped_jitter(
        string? configured,
        int expectedIntervalMinutes,
        int expectedMaxJitterMinutes)
    {
        var options = AppUpdateBackgroundCheckOptions.FromConfiguration(CreateConfiguration(configured));

        Assert.True(options.IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(expectedIntervalMinutes), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(expectedMaxJitterMinutes), options.MaxJitter);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Options_zero_or_negative_interval_disables_background_checks(string configured)
    {
        var options = AppUpdateBackgroundCheckOptions.FromConfiguration(CreateConfiguration(configured));

        Assert.False(options.IsEnabled);
        Assert.Equal(TimeSpan.Zero, options.Interval);
    }

    [Fact]
    public async Task Start_waits_full_interval_before_each_background_check()
    {
        var coordinator = new RecordingCoordinator();
        var delays = new ControlledDelays();
        using var scheduler = new AppUpdateBackgroundCheckScheduler(
            coordinator,
            new AppUpdateBackgroundCheckOptions(TimeSpan.FromMinutes(60), TimeSpan.Zero),
            delays.DelayAsync);

        scheduler.Start();

        // 中文注释：启动闸门刚检查过，后台循环必须先等满一个间隔再检查。
        var first = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromMinutes(60), first.Delay);
        Assert.Equal(0, coordinator.CallCount);

        first.Complete();
        var second = await delays.NextAsync();
        Assert.Equal(1, coordinator.CallCount);

        second.Complete();
        await delays.NextAsync();
        Assert.Equal(2, coordinator.CallCount);

        scheduler.Stop();
        await scheduler.LoopTask.WaitAsync(TestTimeout);
        Assert.Equal(2, coordinator.CallCount);
    }

    [Fact]
    public async Task Start_adds_random_jitter_within_configured_bound()
    {
        var delays = new ControlledDelays();
        using var scheduler = new AppUpdateBackgroundCheckScheduler(
            new RecordingCoordinator(),
            new AppUpdateBackgroundCheckOptions(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10)),
            delays.DelayAsync);

        scheduler.Start();
        var first = await delays.NextAsync();

        Assert.InRange(first.Delay, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(70));

        scheduler.Stop();
        await scheduler.LoopTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Failed_check_is_logged_and_loop_keeps_running()
    {
        var coordinator = new RecordingCoordinator { ThrowOnCall = true };
        var delays = new ControlledDelays();
        using var scheduler = new AppUpdateBackgroundCheckScheduler(
            coordinator,
            new AppUpdateBackgroundCheckOptions(TimeSpan.FromMinutes(60), TimeSpan.Zero),
            delays.DelayAsync);

        scheduler.Start();
        (await delays.NextAsync()).Complete();

        // 中文注释：单次检查异常不能终止循环，下一个周期仍会继续等待并检查。
        var next = await delays.NextAsync();
        Assert.Equal(1, coordinator.CallCount);
        Assert.False(scheduler.LoopTask.IsCompleted);

        scheduler.Stop();
        await scheduler.LoopTask.WaitAsync(TestTimeout);
        Assert.False(next.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Disabled_or_stopped_scheduler_never_checks()
    {
        var coordinator = new RecordingCoordinator();
        var delays = new ControlledDelays();
        using var disabled = new AppUpdateBackgroundCheckScheduler(
            coordinator,
            AppUpdateBackgroundCheckOptions.Create(TimeSpan.Zero),
            delays.DelayAsync);
        using var stopped = new AppUpdateBackgroundCheckScheduler(
            coordinator,
            new AppUpdateBackgroundCheckOptions(TimeSpan.FromMinutes(60), TimeSpan.Zero),
            delays.DelayAsync);

        disabled.Start();
        stopped.Stop();
        stopped.Start();

        await disabled.LoopTask.WaitAsync(TestTimeout);
        await stopped.LoopTask.WaitAsync(TestTimeout);
        Assert.Equal(0, delays.RequestCount);
        Assert.Equal(0, coordinator.CallCount);
    }

    [Fact]
    public async Task Start_is_idempotent()
    {
        var delays = new ControlledDelays();
        using var scheduler = new AppUpdateBackgroundCheckScheduler(
            new RecordingCoordinator(),
            new AppUpdateBackgroundCheckOptions(TimeSpan.FromMinutes(60), TimeSpan.Zero),
            delays.DelayAsync);

        scheduler.Start();
        var loop = scheduler.LoopTask;
        scheduler.Start();
        await delays.NextAsync();

        Assert.Same(loop, scheduler.LoopTask);
        Assert.Equal(1, delays.RequestCount);

        scheduler.Stop();
        await scheduler.LoopTask.WaitAsync(TestTimeout);
    }

    private static IConfiguration CreateConfiguration(string? intervalMinutes)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppUpdateBackgroundCheckOptions.IntervalMinutesConfigurationKey] = intervalMinutes
            })
            .Build();
    }

    private sealed class RecordingCoordinator : IAppUpdateCoordinator
    {
        private int _callCount;

        public bool ThrowOnCall { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AppUpdateCoordinatorResult> CheckForUpdatesAsync(
            bool manual,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Background scheduler must not call foreground checks.");
        }

        public Task<AppUpdateCoordinatorResult> CheckForUpdatesInBackgroundAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            if (ThrowOnCall)
            {
                throw new InvalidOperationException("center unavailable");
            }

            return Task.FromResult(AppUpdateCoordinatorResult.NoUpdate());
        }

        public Task<AppUpdateCoordinatorResult> CheckForUpdatesAtStartupAsync(
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Background scheduler must not call startup checks.");
        }
    }
}
