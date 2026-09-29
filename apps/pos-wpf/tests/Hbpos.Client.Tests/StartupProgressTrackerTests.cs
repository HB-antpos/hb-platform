using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class StartupProgressTrackerTests
{
    // 六个阶段合计 10 秒，便于直接心算进度。
    private static readonly IReadOnlyDictionary<StartupPhase, TimeSpan> Expected = new Dictionary<StartupPhase, TimeSpan>
    {
        [StartupPhase.Services] = TimeSpan.FromSeconds(1),
        [StartupPhase.Interface] = TimeSpan.FromSeconds(2),
        [StartupPhase.Update] = TimeSpan.FromSeconds(1),
        [StartupPhase.Device] = TimeSpan.FromSeconds(1),
        [StartupPhase.Catalog] = TimeSpan.FromSeconds(4),
        [StartupPhase.Display] = TimeSpan.FromSeconds(1)
    };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Progress_follows_real_elapsed_time_inside_expected_budget()
    {
        var tracker = new StartupProgressTracker(Expected, _time);
        Assert.Equal(0, tracker.GetSnapshot().Progress);

        tracker.Enter(StartupPhase.Services);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0.05, tracker.GetSnapshot().Progress, precision: 6);

        tracker.Enter(StartupPhase.Interface);
        _time.Advance(TimeSpan.FromSeconds(1));
        var snapshot = tracker.GetSnapshot();

        // 已完成 Services（1 秒权重）+ Interface 过半（1 秒）= 2 / 10。
        Assert.Equal(0.2, snapshot.Progress, precision: 6);
        Assert.Equal(StartupPhase.Interface, snapshot.CurrentPhase);
        Assert.Equal(TimeSpan.FromSeconds(1), snapshot.CurrentPhaseElapsed);
        Assert.Equal(TimeSpan.FromSeconds(2), snapshot.CurrentPhaseExpected);
        Assert.Equal(TimeSpan.FromSeconds(1.5), snapshot.Elapsed);
        Assert.False(snapshot.IsCompleted);
    }

    [Fact]
    public void Overrunning_phase_slows_down_and_never_reaches_next_phase_boundary()
    {
        var tracker = new StartupProgressTracker(Expected, _time);
        tracker.Enter(StartupPhase.Services);
        tracker.Enter(StartupPhase.Interface);
        tracker.Enter(StartupPhase.Update);
        var boundary = 0.4; // Services + Interface + Update = 4 / 10

        var previous = 0d;
        foreach (var seconds in new[] { 0.5, 1, 2, 5, 30, 600 })
        {
            _time.SetUtcNow(_time.GetUtcNow() + TimeSpan.FromSeconds(seconds));
            var progress = tracker.GetSnapshot().Progress;
            Assert.True(progress >= previous, $"progress moved backwards at +{seconds}s");
            Assert.True(progress < boundary, $"progress {progress} crossed the phase boundary at +{seconds}s");
            previous = progress;
        }

        // 最多推进到本阶段的 95%。
        Assert.InRange(previous, 0.3 + 0.1 * 0.949, 0.3 + 0.1 * 0.9501);
    }

    [Fact]
    public void Entering_a_later_phase_counts_skipped_phases_as_done_and_ignores_going_back()
    {
        var tracker = new StartupProgressTracker(Expected, _time);
        tracker.Enter(StartupPhase.Services);
        _time.Advance(TimeSpan.FromMilliseconds(200));

        // 注册页分支：设备阶段之后直接显示主窗口，跳过"加载商品"。
        tracker.Enter(StartupPhase.Device);
        tracker.Enter(StartupPhase.Display);
        tracker.Enter(StartupPhase.Catalog);

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(StartupPhase.Display, snapshot.CurrentPhase);
        Assert.Equal(0.9, snapshot.Progress, precision: 6);
    }

    [Fact]
    public void Complete_reports_full_progress_and_freezes_elapsed_time()
    {
        var tracker = new StartupProgressTracker(Expected, _time);
        tracker.Enter(StartupPhase.Services);
        _time.Advance(TimeSpan.FromSeconds(3));

        tracker.Complete();
        _time.Advance(TimeSpan.FromSeconds(10));
        tracker.Enter(StartupPhase.Catalog);

        var snapshot = tracker.GetSnapshot();
        Assert.True(snapshot.IsCompleted);
        Assert.True(tracker.IsCompleted);
        Assert.Equal(1, snapshot.Progress);
        Assert.Null(snapshot.CurrentPhase);
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot.Elapsed);
    }

    [Fact]
    public void Measured_durations_are_contiguous_and_skip_missing_or_interrupted_phases()
    {
        var tracker = new StartupProgressTracker(Expected, _time);
        Assert.Empty(tracker.GetMeasuredDurations());

        tracker.Enter(StartupPhase.Services);
        _time.Advance(TimeSpan.FromMilliseconds(400));
        tracker.Enter(StartupPhase.Interface);
        _time.Advance(TimeSpan.FromMilliseconds(900));
        tracker.Enter(StartupPhase.Update);
        // 启动更新弹框等待收银员操作：这段时间不能算进本机的"检查更新"耗时。
        tracker.MarkCurrentPhaseInterrupted();
        _time.Advance(TimeSpan.FromSeconds(20));
        tracker.Enter(StartupPhase.Device);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        tracker.Enter(StartupPhase.Display);
        _time.Advance(TimeSpan.FromMilliseconds(250));

        // 未完成的启动不产生样本，避免把失败或中断的启动写进档案。
        Assert.Empty(tracker.GetMeasuredDurations());

        tracker.Complete();
        tracker.MarkCurrentPhaseInterrupted();

        var measured = tracker.GetMeasuredDurations()
            .OrderBy(pair => pair.Key)
            .ToArray();
        Assert.Equal(
            new[]
            {
                KeyValuePair.Create(StartupPhase.Services, TimeSpan.FromMilliseconds(400)),
                KeyValuePair.Create(StartupPhase.Interface, TimeSpan.FromMilliseconds(900)),
                KeyValuePair.Create(StartupPhase.Device, TimeSpan.FromMilliseconds(300)),
                KeyValuePair.Create(StartupPhase.Display, TimeSpan.FromMilliseconds(250))
            },
            measured);
    }

    [Fact]
    public void HasEntered_reports_only_phases_that_actually_ran()
    {
        var tracker = new StartupProgressTracker(Expected, _time);
        tracker.Enter(StartupPhase.Services);
        // 设备未注册：设备阶段后直接显示注册页，"加载商品"从未执行，启动页不能记住占位门店。
        tracker.Enter(StartupPhase.Device);
        tracker.Enter(StartupPhase.Display);
        tracker.Complete();

        Assert.True(tracker.HasEntered(StartupPhase.Services));
        Assert.False(tracker.HasEntered(StartupPhase.Interface));
        Assert.True(tracker.HasEntered(StartupPhase.Device));
        Assert.False(tracker.HasEntered(StartupPhase.Catalog));
        Assert.True(tracker.HasEntered(StartupPhase.Display));
    }

    [Fact]
    public void Missing_or_non_positive_expectations_fall_back_to_defaults()
    {
        var tracker = new StartupProgressTracker(
            new Dictionary<StartupPhase, TimeSpan> { [StartupPhase.Update] = TimeSpan.Zero },
            _time);

        foreach (var phase in StartupProgressTracker.Phases)
        {
            Assert.Equal(StartupTimingProfile.DefaultExpectedDurations[phase], tracker.GetExpectedDuration(phase));
        }
    }

    [Fact]
    public void PhaseFraction_is_linear_then_asymptotic_and_continuous()
    {
        Assert.Equal(0, StartupProgressTracker.PhaseFraction(-1));
        Assert.Equal(0, StartupProgressTracker.PhaseFraction(double.NaN));
        Assert.Equal(0.25, StartupProgressTracker.PhaseFraction(0.25), precision: 12);
        Assert.Equal(0.8, StartupProgressTracker.PhaseFraction(0.8), precision: 12);

        // 0.8 处左右两侧的斜率都约等于 1（导数连续，进度条不会出现拐点顿挫）。
        var slope = (StartupProgressTracker.PhaseFraction(0.8 + 1e-6) - StartupProgressTracker.PhaseFraction(0.8)) / 1e-6;
        Assert.InRange(slope, 0.999, 1.001);

        var previous = 0d;
        for (var x = 0.0; x <= 50; x += 0.05)
        {
            var value = StartupProgressTracker.PhaseFraction(x);
            Assert.True(value >= previous);
            Assert.True(value < 0.95 + 1e-12);
            previous = value;
        }
    }

    [Fact]
    public void SmoothTowards_catches_up_quickly_and_never_moves_backwards()
    {
        var frame = TimeSpan.FromMilliseconds(33);
        var displayed = 0d;
        var frames = 0;
        while (displayed < 0.6 && frames < 100)
        {
            displayed = StartupProgressTracker.SmoothTowards(displayed, 0.6, frame);
            frames++;
        }

        Assert.Equal(0.6, displayed, precision: 12);
        // 90ms 时间常数：约 0.5 秒内追上一次 60% 的跳变，远快于肉眼可感的"卡顿"。
        Assert.InRange(frames, 5, 20);
        Assert.Equal(0.6, StartupProgressTracker.SmoothTowards(0.6, 0.3, frame));
        Assert.Equal(0.6, StartupProgressTracker.SmoothTowards(0.6, 0.6, TimeSpan.Zero));
    }
}
