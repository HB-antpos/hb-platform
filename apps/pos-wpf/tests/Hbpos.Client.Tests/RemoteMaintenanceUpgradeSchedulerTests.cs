using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 夜间 RustDesk 升级的时机：与 WPF 夜间无人值守安装同一个窗口，并要求连续两轮收银空闲；
/// 失败后隔 30 分钟再试，白天一律不动。
/// </summary>
public sealed class RemoteMaintenanceUpgradeSchedulerTests
{
    [Fact]
    public async Task Daytime_never_upgrades_even_when_idle()
    {
        var harness = new Harness(new TimeOnly(10, 0));

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();
        await harness.AdvanceAndTickAsync();

        Assert.Equal(0, harness.Upgrader.CallCount);
    }

    [Fact]
    public async Task Night_window_needs_two_idle_ticks_before_upgrading()
    {
        var harness = new Harness(new TimeOnly(23, 0));

        await harness.TickAsync();
        Assert.Equal(0, harness.Upgrader.CallCount);

        await harness.AdvanceAndTickAsync();
        Assert.Equal(1, harness.Upgrader.CallCount);
    }

    [Fact]
    public async Task An_active_transaction_resets_the_idle_count()
    {
        var harness = new Harness(new TimeOnly(23, 0));

        await harness.TickAsync();
        harness.Guard.CanInstall = false;
        await harness.AdvanceAndTickAsync();
        harness.Guard.CanInstall = true;
        await harness.AdvanceAndTickAsync();
        Assert.Equal(0, harness.Upgrader.CallCount);

        await harness.AdvanceAndTickAsync();
        Assert.Equal(1, harness.Upgrader.CallCount);
    }

    [Fact]
    public async Task Thursday_evening_waits_until_the_later_window_start()
    {
        // 2026-10-01 是周四：19:00 仍在营业，20:00 起才进入窗口。
        var harness = new Harness(new TimeOnly(19, 0), date: new DateOnly(2026, 10, 1));

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();
        Assert.Equal(0, harness.Upgrader.CallCount);
    }

    [Fact]
    public async Task A_failed_upgrade_waits_for_the_retry_interval()
    {
        var harness = new Harness(new TimeOnly(23, 0));
        harness.Upgrader.Result = RemoteMaintenanceUpgradeResult.Failed;

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();
        Assert.Equal(1, harness.Upgrader.CallCount);

        // 30 分钟内的几轮都不再尝试。
        for (var i = 0; i < 5; i++) await harness.AdvanceAndTickAsync();
        Assert.Equal(1, harness.Upgrader.CallCount);

        await harness.AdvanceAndTickAsync();
        Assert.Equal(2, harness.Upgrader.CallCount);
    }

    [Fact]
    public async Task Upgrader_exceptions_do_not_break_the_loop()
    {
        var harness = new Harness(new TimeOnly(23, 0));
        harness.Upgrader.Failure = new IOException("disk");

        await harness.TickAsync();
        await harness.AdvanceAndTickAsync();

        Assert.Equal(1, harness.Upgrader.CallCount);
    }

    [Fact]
    public void Disabled_window_never_starts_the_loop()
    {
        var harness = new Harness(new TimeOnly(23, 0), AppUpdateUnattendedInstallOptions.Disabled);

        harness.Scheduler.Start();

        Assert.True(harness.Scheduler.LoopTask.IsCompleted);
    }

    private sealed class Harness
    {
        // 默认日期 2026-09-28 是周一，不受周四推迟影响；时区固定为 UTC。
        public Harness(TimeOnly localTime, AppUpdateUnattendedInstallOptions? options = null, DateOnly? date = null)
        {
            var day = date ?? new DateOnly(2026, 9, 28);
            Time = new FakeTimeProvider(new DateTimeOffset(day.ToDateTime(localTime), TimeSpan.Zero));
            Time.SetLocalTimeZone(TimeZoneInfo.Utc);
            Scheduler = new RemoteMaintenanceUpgradeScheduler(
                Upgrader, Guard, options ?? AppUpdateUnattendedInstallOptions.Default, Time);
        }

        public FakeTimeProvider Time { get; }

        public RecordingUpgrader Upgrader { get; } = new();

        public ToggleGuard Guard { get; } = new();

        public RemoteMaintenanceUpgradeScheduler Scheduler { get; }

        public Task TickAsync() => Scheduler.TickAsync(CancellationToken.None);

        public Task AdvanceAndTickAsync()
        {
            Time.Advance(RemoteMaintenanceUpgradeScheduler.TickInterval);
            return TickAsync();
        }
    }

    private sealed class RecordingUpgrader : IRemoteMaintenanceUpgrader
    {
        public RemoteMaintenanceUpgradeResult Result { get; set; } = RemoteMaintenanceUpgradeResult.Upgraded;

        public Exception? Failure { get; set; }

        public int CallCount { get; private set; }

        public Task<RemoteMaintenanceUpgradeResult> ApplyPendingUpgradeAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Result);
        }
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
}
