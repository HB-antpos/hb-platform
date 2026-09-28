using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class AppUpdateUnattendedInstallSchedulerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(null, false, true, "01:00", "07:00")]
    [InlineData("", false, true, "01:00", "07:00")]
    [InlineData(null, true, false, "01:00", "07:00")]
    [InlineData("off", false, false, "01:00", "07:00")]
    [InlineData("02:30-05:00", true, true, "02:30", "05:00")]
    [InlineData("23:00-05:00", false, true, "23:00", "05:00")]
    [InlineData("1-7", false, true, "01:00", "07:00")]
    [InlineData("03:00-03:00", true, false, "01:00", "07:00")]
    public void Options_read_window_from_configuration_and_default_off_in_debug(
        string? configured,
        bool isDebugBuild,
        bool expectedEnabled,
        string expectedStart,
        string expectedEnd)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppUpdateUnattendedInstallOptions.WindowConfigurationKey] = configured
            })
            .Build();

        var options = AppUpdateUnattendedInstallOptions.FromConfiguration(configuration, isDebugBuild);

        Assert.Equal(expectedEnabled, options.IsEnabled);
        Assert.Equal(TimeOnly.Parse(expectedStart), options.WindowStart);
        Assert.Equal(TimeOnly.Parse(expectedEnd), options.WindowEnd);
    }

    [Theory]
    [InlineData("00:59", false)]
    [InlineData("01:00", true)]
    [InlineData("06:59", true)]
    [InlineData("07:00", false)]
    [InlineData("13:00", false)]
    public void Default_window_is_1am_to_7am_local_time(string localTime, bool expected)
    {
        Assert.Equal(expected, AppUpdateUnattendedInstallOptions.Default.IsWithinWindow(TimeOnly.Parse(localTime)));
    }

    [Theory]
    [InlineData("22:59", false)]
    [InlineData("23:00", true)]
    [InlineData("02:00", true)]
    [InlineData("05:00", false)]
    public void Window_can_cross_midnight(string localTime, bool expected)
    {
        var options = new AppUpdateUnattendedInstallOptions(true, new TimeOnly(23, 0), new TimeOnly(5, 0));

        Assert.Equal(expected, options.IsWithinWindow(TimeOnly.Parse(localTime)));
    }

    [Fact]
    public void Windows_elevation_probe_reports_a_reason_without_throwing()
    {
        var canElevate = new WindowsAppUpdateElevationProbe().CanElevateWithoutPrompt(out var reason);

        // 中文注释：结果取决于本机 UAC 设置，这里只保证真实令牌与注册表读取不会抛错。
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(canElevate, reason is "administrator-token" or "administrator-elevates-without-prompt");
    }

    [Fact]
    public async Task Installs_after_two_idle_ticks_inside_window_regardless_of_update_prompts()
    {
        var harness = new Harness(localTime: new TimeOnly(1, 0));

        await harness.TickAsync();
        Assert.Equal(0, harness.Coordinator.CallCount);

        // 中文注释：连续两轮空闲（至少 5 分钟）才安装。
        await harness.AdvanceAndTickAsync();
        Assert.Equal(1, harness.Coordinator.CallCount);
    }

    [Fact]
    public async Task Does_nothing_outside_window()
    {
        var harness = new Harness(localTime: new TimeOnly(7, 0));

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();
        await harness.AdvanceAndTickAsync();

        Assert.Equal(0, harness.Coordinator.CallCount);
    }

    [Fact]
    public async Task Active_transaction_restarts_idle_count()
    {
        var harness = new Harness(localTime: new TimeOnly(2, 0));

        await harness.TickAsync();
        harness.Guard.CanInstall = false;
        await harness.AdvanceAndTickAsync();
        harness.Guard.CanInstall = true;
        await harness.AdvanceAndTickAsync();

        Assert.Equal(0, harness.Coordinator.CallCount);

        await harness.AdvanceAndTickAsync();
        Assert.Equal(1, harness.Coordinator.CallCount);
    }

    [Fact]
    public async Task Skips_rest_of_night_when_uac_would_prompt_and_retries_next_night()
    {
        var harness = new Harness(localTime: new TimeOnly(1, 0));
        harness.Probe.CanElevate = false;

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();
        harness.Probe.CanElevate = true;
        await harness.AdvanceAndTickAsync();

        // 中文注释：会弹 UAC 时无人确认会卡住 POS，当晚不再尝试。
        Assert.Equal(0, harness.Coordinator.CallCount);
        Assert.Equal(1, harness.Probe.CallCount);

        await harness.AdvanceToNextNightAsync();
        Assert.Equal(1, harness.Coordinator.CallCount);
    }

    [Fact]
    public async Task No_update_waits_30_minutes_before_checking_again()
    {
        var harness = new Harness(localTime: new TimeOnly(1, 0));
        harness.Coordinator.Enqueue(AppUpdateCoordinatorResult.NoUpdate());

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();
        Assert.Equal(1, harness.Coordinator.CallCount);

        for (var i = 0; i < 5; i++)
        {
            await harness.AdvanceAndTickAsync();
        }

        Assert.Equal(1, harness.Coordinator.CallCount);

        await harness.AdvanceAndTickAsync();
        Assert.Equal(2, harness.Coordinator.CallCount);
    }

    [Fact]
    public async Task Stops_for_the_night_after_three_launch_failures()
    {
        var harness = new Harness(localTime: new TimeOnly(1, 0));
        for (var i = 0; i < 3; i++)
        {
            harness.Coordinator.Enqueue(AppUpdateCoordinatorResult.FromStatus(
                AppUpdateCoordinatorStatus.InstallFailed,
                "launch failed"));
        }

        await harness.TickAsync();
        for (var i = 0; i < 6; i++)
        {
            await harness.AdvanceAndTickAsync();
        }

        Assert.Equal(3, harness.Coordinator.CallCount);
    }

    [Fact]
    public async Task Non_silent_installer_is_skipped_for_the_night()
    {
        var harness = new Harness(localTime: new TimeOnly(1, 0));
        harness.Coordinator.Enqueue(Skipped(AppUpdateCoordinator.UnattendedInstallerNotSilentErrorCode));

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();
        for (var i = 0; i < 10; i++)
        {
            await harness.AdvanceAndTickAsync();
        }

        Assert.Equal(1, harness.Coordinator.CallCount);
    }

    [Fact]
    public async Task Transaction_started_before_launch_waits_for_idle_again()
    {
        var harness = new Harness(localTime: new TimeOnly(1, 0));
        harness.Coordinator.Enqueue(Skipped(AppUpdateCoordinator.UnattendedActiveTransactionErrorCode));

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();
        await harness.AdvanceAndTickAsync();
        Assert.Equal(1, harness.Coordinator.CallCount);

        await harness.AdvanceAndTickAsync();
        Assert.Equal(2, harness.Coordinator.CallCount);
    }

    [Fact]
    public async Task Start_ticks_every_five_minutes_until_stopped()
    {
        var harness = new Harness(localTime: new TimeOnly(1, 0));
        var delays = new ControlledDelays();
        using var scheduler = harness.CreateScheduler(delays.DelayAsync);

        scheduler.Start();
        var first = await delays.NextAsync();
        Assert.Equal(AppUpdateUnattendedInstallScheduler.TickInterval, first.Delay);

        first.Complete();
        (await delays.NextAsync()).Complete();
        await delays.NextAsync();
        Assert.Equal(1, harness.Coordinator.CallCount);

        scheduler.Stop();
        await scheduler.LoopTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Disabled_options_never_start()
    {
        var harness = new Harness(localTime: new TimeOnly(1, 0), options: AppUpdateUnattendedInstallOptions.Disabled);
        var delays = new ControlledDelays();
        using var scheduler = harness.CreateScheduler(delays.DelayAsync);

        scheduler.Start();
        await scheduler.LoopTask.WaitAsync(TestTimeout);

        Assert.Equal(0, delays.RequestCount);
        Assert.Equal(0, harness.Coordinator.CallCount);
    }

    private static AppUpdateCoordinatorResult Skipped(string errorCode)
    {
        return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.UnattendedInstallSkipped) with
        {
            ErrorCode = errorCode
        };
    }

    private sealed class Harness
    {
        private readonly AppUpdateUnattendedInstallScheduler _scheduler;

        public Harness(TimeOnly localTime, AppUpdateUnattendedInstallOptions? options = null)
        {
            Options = options ?? AppUpdateUnattendedInstallOptions.Default;
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, localTime.Hour, localTime.Minute, 0, TimeSpan.Zero));
            Time.SetLocalTimeZone(TimeZoneInfo.Utc);
            _scheduler = CreateScheduler(delayAsync: null);
        }

        public AppUpdateUnattendedInstallOptions Options { get; }

        public FakeTimeProvider Time { get; }

        public RecordingCoordinator Coordinator { get; } = new();

        public ToggleGuard Guard { get; } = new();

        public ToggleElevationProbe Probe { get; } = new();

        public AppUpdateUnattendedInstallScheduler CreateScheduler(Func<TimeSpan, CancellationToken, Task>? delayAsync)
        {
            return new AppUpdateUnattendedInstallScheduler(Coordinator, Guard, Probe, Options, Time, delayAsync);
        }

        public Task TickAsync() => _scheduler.TickAsync(CancellationToken.None);

        public Task AdvanceAndTickAsync()
        {
            Time.Advance(AppUpdateUnattendedInstallScheduler.TickInterval);
            return TickAsync();
        }

        public async Task AdvanceToNextNightAsync()
        {
            // 中文注释：先走出窗口让当晚状态复位，再回到次日 01:00 连续两轮空闲。
            Time.Advance(TimeSpan.FromHours(7));
            await TickAsync();
            Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromHours(7) - AppUpdateUnattendedInstallScheduler.TickInterval * 3);
            await TickAsync();
            await AdvanceAndTickAsync();
            await AdvanceAndTickAsync();
        }
    }

    private sealed class RecordingCoordinator : IAppUpdateCoordinator
    {
        private readonly Queue<AppUpdateCoordinatorResult> _results = new();

        public int CallCount { get; private set; }

        public void Enqueue(AppUpdateCoordinatorResult result) => _results.Enqueue(result);

        public Task<AppUpdateCoordinatorResult> InstallUpdateUnattendedAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.AlreadyRunning));
        }

        public Task<AppUpdateCoordinatorResult> CheckForUpdatesAsync(
            bool manual,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unattended scheduler must not call foreground checks.");

        public Task<AppUpdateCoordinatorResult> CheckForUpdatesAtStartupAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unattended scheduler must not call startup checks.");

        public Task<AppUpdateCoordinatorResult> CheckForUpdatesInBackgroundAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unattended scheduler must not call background checks.");
    }

    private sealed class ToggleGuard : IAppUpdateInstallSafetyGuard
    {
        public bool CanInstall { get; set; } = true;

        public bool CanInstallUpdate(out string statusKey, out object[] args)
        {
            statusKey = CanInstall ? string.Empty : "appUpdate.install.activeTransaction";
            args = [];
            return CanInstall;
        }
    }

    private sealed class ToggleElevationProbe : IAppUpdateElevationProbe
    {
        public bool CanElevate { get; set; } = true;

        public int CallCount { get; private set; }

        public bool CanElevateWithoutPrompt(out string reason)
        {
            CallCount++;
            reason = CanElevate ? "test-allows" : "test-prompts";
            return CanElevate;
        }
    }
}
