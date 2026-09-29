using System.Globalization;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class StartupProgressStateTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    [Fact]
    public void Initial_state_shows_first_phase_zero_percent_and_generic_subtitle()
    {
        var state = CreateState("en-US");

        Assert.Equal("HB POS", state.TitleText);
        Assert.Equal("Point of sale", state.SubtitleText);
        Assert.Equal("Starting services", state.StageTitle);
        Assert.Equal("Preparing the local database and background services", state.StageDetail);
        Assert.Equal("0", state.PercentText);
        Assert.Equal("Step 1 of 6", state.StepText);
        Assert.Equal("0.0s elapsed", state.ElapsedText);
        Assert.Equal(StartupStepState.Active, state.Steps[0].State);
        Assert.All(state.Steps.Skip(1), step => Assert.Equal(StartupStepState.Pending, step.State));
    }

    [Fact]
    public void Remembered_store_label_replaces_generic_subtitle()
    {
        var state = CreateState("zh-CN", storeLabel: "  Morayfield · POS-03 ");

        Assert.Equal("Morayfield · POS-03", state.SubtitleText);
        Assert.Equal("正在启动服务", state.StageTitle);
        Assert.Equal("第 1/6 步", state.StepText);
    }

    [Fact]
    public void Percent_never_shows_100_before_startup_really_completes()
    {
        var state = CreateState("en-US");

        for (var frame = 0; frame < 200; frame++)
        {
            state.Apply(Snapshot(1, StartupPhase.Display), Frame);
        }

        Assert.Equal(99, state.Percent);
        Assert.Equal("99", state.PercentText);

        state.Apply(Completed(), TimeSpan.FromSeconds(1));

        Assert.Equal(100, state.Percent);
        Assert.Equal(100, state.ProgressValue, precision: 6);
        Assert.Equal("Ready", state.StageTitle);
        Assert.Equal(string.Empty, state.StageDetail);
        Assert.Equal("Step 6 of 6", state.StepText);
        Assert.All(state.Steps, step => Assert.Equal(StartupStepState.Done, step.State));
    }

    [Fact]
    public void Displayed_progress_catches_up_smoothly_and_never_moves_backwards()
    {
        var state = CreateState("en-US");

        state.Apply(Snapshot(0.5, StartupPhase.Update), Frame);
        var afterOneFrame = state.ProgressValue;
        Assert.InRange(afterOneFrame, 0.1, 49.9);

        for (var frame = 0; frame < 30; frame++)
        {
            state.Apply(Snapshot(0.5, StartupPhase.Update), Frame);
        }

        Assert.Equal(50, state.ProgressValue, precision: 6);

        // 追踪器给出更小的目标（理论上不会发生）时显示值保持不动。
        state.Apply(Snapshot(0.2, StartupPhase.Update), Frame);
        Assert.Equal(50, state.ProgressValue, precision: 6);
    }

    [Fact]
    public void Steps_mark_previous_phases_done_and_current_phase_active()
    {
        var state = CreateState("en-US");

        state.Apply(Snapshot(0.6, StartupPhase.Device), Frame);

        Assert.Equal(
            new[]
            {
                StartupStepState.Done,
                StartupStepState.Done,
                StartupStepState.Done,
                StartupStepState.Active,
                StartupStepState.Pending,
                StartupStepState.Pending
            },
            state.Steps.Select(step => step.State).ToArray());
        Assert.Equal("Verifying this device", state.StageTitle);
        Assert.Equal("Step 4 of 6", state.StepText);
    }

    [Theory]
    [InlineData(StartupPhase.Update, 3500, 600, true)]
    [InlineData(StartupPhase.Device, 3200, 500, true)]
    // 未到 3 秒下限：即使已超出预期很多倍也不提示。
    [InlineData(StartupPhase.Update, 2900, 300, false)]
    // 超过 3 秒但不到本机常见耗时的 2 倍：属于正常波动。
    [InlineData(StartupPhase.Update, 3500, 2000, false)]
    // 本地阶段慢不是服务器的问题，不显示网络提示。
    [InlineData(StartupPhase.Catalog, 10000, 800, false)]
    public void Slow_network_hint_only_for_network_phases_that_are_clearly_slow(
        StartupPhase phase,
        int elapsedMs,
        int expectedMs,
        bool expectedSlow)
    {
        var state = CreateState("en-US");

        state.Apply(
            new StartupProgressSnapshot(
                0.4,
                phase,
                TimeSpan.FromSeconds(6),
                TimeSpan.FromMilliseconds(elapsedMs),
                TimeSpan.FromMilliseconds(expectedMs),
                false),
            Frame);

        Assert.Equal(expectedSlow, state.IsStageSlow);
        if (expectedSlow)
        {
            Assert.Equal($"The server is slow to respond ({elapsedMs / 1000}s)", state.StageDetail);
        }
        else
        {
            Assert.DoesNotContain("slow", state.StageDetail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Slow_network_hint_is_localized()
    {
        var state = CreateState("zh-CN");

        state.Apply(
            new StartupProgressSnapshot(0.4, StartupPhase.Update, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(8.6), TimeSpan.FromMilliseconds(700), false),
            Frame);

        Assert.True(state.IsStageSlow);
        Assert.Equal("正在检查更新", state.StageTitle);
        Assert.Equal("服务器响应较慢，已等待 8 秒", state.StageDetail);
        Assert.Equal("已用 9.0 秒", state.ElapsedText);
    }

    [Theory]
    [InlineData("en-US", false, "Updated to 1.9.0")]
    [InlineData("en-US", true, "Switched back to 1.9.0")]
    [InlineData("zh-CN", false, "已更新到 1.9.0")]
    [InlineData("zh-CN", true, "已回退到 1.9.0")]
    public void SetVersion_shows_localized_update_notice(string culture, bool isRollback, string expectedNotice)
    {
        var state = CreateState(culture);

        state.SetVersion("1.9.0", new AppLaunchVersionNotice("1.9.0", isRollback));

        Assert.Equal("1.9.0", state.VersionText);
        Assert.Equal(expectedNotice, state.UpdateNoticeText);
        Assert.True(state.HasUpdateNotice);
    }

    [Fact]
    public void SetVersion_without_notice_hides_update_notice()
    {
        var state = CreateState("en-US");
        var changedProperties = new List<string>();
        state.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName ?? string.Empty);

        state.SetVersion("1.8.3", null);

        Assert.Equal("1.8.3", state.VersionText);
        Assert.False(state.HasUpdateNotice);
        Assert.Contains(nameof(StartupProgressState.VersionText), changedProperties);
    }

    [Theory]
    [InlineData("en-US", "Version")]
    [InlineData("zh-CN", "版本")]
    public void Version_label_is_localized(string culture, string expected)
    {
        Assert.Equal(expected, CreateState(culture).VersionLabel);
    }

    [Theory]
    [InlineData("zh-CN", "zh-CN", true)]
    [InlineData("ZH-cn", "zh-CN", true)]
    [InlineData("en-AU", "en-US", false)]
    [InlineData(null, "en-US", false)]
    public void Startup_culture_falls_back_to_default_when_unsupported(string? name, string expected, bool supported)
    {
        Assert.Equal(supported, LocalizationService.TryGetSupportedCulture(name, out var culture));
        Assert.Equal(expected, culture.Name);
    }

    private static StartupProgressState CreateState(string cultureName, string? storeLabel = null)
    {
        Assert.True(LocalizationService.TryGetSupportedCulture(cultureName, out var culture));
        return new StartupProgressState(key => LocalizationService.Translate(key, culture), culture, storeLabel);
    }

    private static StartupProgressSnapshot Snapshot(double progress, StartupPhase phase) =>
        new(progress, phase, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), false);

    private static StartupProgressSnapshot Completed() =>
        new(1, null, TimeSpan.FromSeconds(4), TimeSpan.Zero, TimeSpan.Zero, true);
}
