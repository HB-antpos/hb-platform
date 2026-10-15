using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>重启浮层的展示状态：模式切换、按钮结果、文案与互斥保护（不涉及真实 WPF 控件）。</summary>
public sealed class WpfRestartPromptServiceTests
{
    [Fact]
    public async Task Prompt_primary_returns_restart_now_and_secondary_returns_later()
    {
        using var ui = CreateUi();

        var restart = ui.ShowPromptAsync(new RestartPromptInfo(TimeSpan.FromDays(9.2), 0), CancellationToken.None);
        Assert.True(ui.IsOpen);
        Assert.True(ui.IsPromptMode);
        ui.PrimaryCommand.Execute(null);
        Assert.Equal(RestartPromptChoice.RestartNow, await restart);

        ui.Hide();
        var later = ui.ShowPromptAsync(new RestartPromptInfo(TimeSpan.FromDays(9.2), 0), CancellationToken.None);
        ui.SecondaryCommand.Execute(null);
        Assert.Equal(RestartPromptChoice.Later, await later);
    }

    [Fact]
    public void Prompt_text_shows_whole_days_and_hours_in_chinese()
    {
        using var ui = CreateUi();

        _ = ui.ShowPromptAsync(new RestartPromptInfo(TimeSpan.FromDays(9) + TimeSpan.FromHours(3), 4), CancellationToken.None);

        Assert.Equal("9 天 3 小时", ui.UptimeFactText);
        Assert.Contains("9 天", ui.BodyText);
        Assert.Equal("还有 4 条待传", ui.UploadsFactText);
        Assert.Equal("Warn", ui.UploadsFactTone);
        Assert.Equal("立即重启", ui.PrimaryText);
        Assert.Equal("稍后", ui.SecondaryText);
    }

    [Fact]
    public async Task Countdown_cancel_and_progress()
    {
        using var ui = CreateUi();

        var action = ui.ShowCountdown(30, CancellationToken.None);
        ui.SetCountdownRemaining(15);

        Assert.True(ui.IsCountdownMode);
        Assert.Equal(15, ui.CountdownRemaining);
        Assert.Equal(0.5, ui.CountdownProgress, precision: 3);
        ui.SecondaryCommand.Execute(null);
        Assert.Equal(RestartCountdownAction.Cancel, await action);
    }

    [Fact]
    public async Task Blocked_lists_hard_issues_and_supports_all_three_choices()
    {
        using var ui = CreateUi();
        var report = new RestartSafetyReport(
        [
            new RestartSafetyIssue(RestartSafetyIssueCode.CartNotEmpty, RestartSafetySeverity.Hard, 3),
            new RestartSafetyIssue(RestartSafetyIssueCode.CardRecoveryOpen, RestartSafetySeverity.Hard, 1),
            new RestartSafetyIssue(RestartSafetyIssueCode.UploadPending, RestartSafetySeverity.Soft, 2)
        ]);

        var recheck = ui.ShowBlockedAsync(report, canGoToPos: true, CancellationToken.None);

        Assert.True(ui.IsBlockedMode);
        Assert.True(ui.HasTertiary);
        Assert.Equal(2, ui.Issues.Count);              // 软问题不列入阻止清单
        Assert.Equal("收银台还有 3 件商品没有结账", ui.Issues[0].Text);
        Assert.Equal("有 1 笔刷卡结果还没确认", ui.Issues[1].Text);
        ui.PrimaryCommand.Execute(null);
        Assert.Equal(RestartBlockedChoice.Recheck, await recheck);

        var goPos = ui.ShowBlockedAsync(report, canGoToPos: true, CancellationToken.None);
        ui.TertiaryCommand.Execute(null);
        Assert.Equal(RestartBlockedChoice.GoToPos, await goPos);

        var dismiss = ui.ShowBlockedAsync(report, canGoToPos: false, CancellationToken.None);
        Assert.False(ui.HasTertiary);
        ui.SecondaryCommand.Execute(null);
        Assert.Equal(RestartBlockedChoice.Dismiss, await dismiss);
    }

    [Fact]
    public async Task Failure_shows_reference_and_completes_on_ok()
    {
        using var ui = CreateUi();

        var done = ui.ShowFailureAsync(
            new RestartFailureInfo(5, "Access is denied.", "RB-20261015-ABCD", TimeSpan.FromDays(9)),
            CancellationToken.None);

        Assert.True(ui.IsFailedMode);
        Assert.True(ui.IsDangerTone);
        Assert.Contains("code=5", ui.FailureDetailText);
        Assert.Contains("RB-20261015-ABCD", ui.FailureDetailText);
        ui.PrimaryCommand.Execute(null);
        await done;
    }

    [Fact]
    public async Task Cancellation_token_cancels_pending_prompt()
    {
        using var ui = CreateUi();
        using var cts = new CancellationTokenSource();

        var prompt = ui.ShowPromptAsync(new RestartPromptInfo(TimeSpan.FromDays(8), 0), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt);
    }

    [Fact]
    public async Task Second_prompt_is_rejected_while_the_overlay_is_busy()
    {
        using var ui = CreateUi();
        _ = ui.ShowPromptAsync(new RestartPromptInfo(TimeSpan.FromDays(8), 0), CancellationToken.None);

        var second = ui.ShowPromptAsync(new RestartPromptInfo(TimeSpan.FromDays(8), 0), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(ui.IsPromptMode);   // 原来的提示不受影响
    }

    [Fact]
    public void Hide_closes_overlay_and_clears_mode()
    {
        using var ui = CreateUi();
        _ = ui.ShowCountdown(30, CancellationToken.None);

        ui.Hide();

        Assert.False(ui.IsOpen);
        Assert.False(ui.HasPrimary);
        Assert.Empty(ui.Issues);
    }

    [Fact]
    public void Working_mode_has_no_buttons()
    {
        using var ui = CreateUi();

        ui.ShowWorking();

        Assert.True(ui.IsWorkingMode);
        Assert.False(ui.HasPrimary);
        Assert.False(ui.HasSecondary);
    }

    private static WpfRestartPromptService CreateUi()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        return new WpfRestartPromptService(localization);
    }
}
