using Hbpos.Updater;

namespace Hbpos.Client.Tests;

public sealed class UpdaterViewModelTests
{
    private static readonly UpdaterStrings Strings = UpdaterStrings.ChineseSimplified;

    [Fact]
    public void Starts_on_closing_stage_with_indeterminate_progress()
    {
        var viewModel = new UpdaterViewModel(Strings, "1.8.3", "1.9.0");

        viewModel.ShowClosing();

        Assert.Equal(UpdaterStage.Closing, viewModel.Stage);
        Assert.Equal("正在更新 HB POS", viewModel.Title);
        Assert.Equal("正在关闭旧版本…", viewModel.StageTitle);
        Assert.Equal("准备中", viewModel.PercentText);
        Assert.False(viewModel.IsPercentNumeric);
        Assert.True(viewModel.IsIndeterminate);
        Assert.Equal("已用时 00:00", viewModel.ElapsedText);
        Assert.Equal("通常只需几秒", viewModel.HintText);
        Assert.True(viewModel.HasVersions);
        Assert.False(viewModel.CanClose);
        Assert.Equal(UpdaterStepState.Done, viewModel.DownloadStep.State);
        Assert.Equal(UpdaterStepState.Active, viewModel.InstallStep.State);
        Assert.Equal(UpdaterStepState.Pending, viewModel.LaunchStep.State);
    }

    [Fact]
    public void Installing_before_first_progress_hints_at_windows_permission_prompt()
    {
        var viewModel = new UpdaterViewModel(Strings, "1.8.3", "1.9.0");

        viewModel.ShowInstalling();
        viewModel.UpdateElapsed(TimeSpan.FromSeconds(4));

        Assert.Equal("正在启动安装程序", viewModel.StageDetail);
        Assert.Equal("如出现授权窗口，请选择「是」", viewModel.HintText);
        Assert.True(viewModel.IsIndeterminate);
    }

    [Fact]
    public void Installer_progress_switches_to_determinate_percent()
    {
        var viewModel = new UpdaterViewModel(Strings, "1.8.3", "1.9.0");
        viewModel.ShowInstalling();

        viewModel.ReportInstallerProgress(new InstallerProgressSnapshot(InstallerProgressStage.Installing, 62));

        Assert.Equal("62%", viewModel.PercentText);
        Assert.True(viewModel.IsPercentNumeric);
        Assert.True(viewModel.IsDeterminate);
        Assert.Equal(62, viewModel.ProgressValue);
        Assert.Equal("正在替换程序文件", viewModel.StageDetail);
    }

    [Theory]
    [InlineData(50, 10, "已用时 00:10", "预计还需约 10 秒")]
    [InlineData(20, 60, "已用时 01:00", "预计还需约 4 分钟")]
    [InlineData(99, 30, "已用时 00:30", "预计还需约 1 秒")]
    [InlineData(4, 30, "已用时 00:30", "正在估算剩余时间")]
    [InlineData(60, 2, "已用时 00:02", "正在估算剩余时间")]
    public void Remaining_time_is_estimated_from_elapsed_time_and_percent(
        int percent,
        int elapsedSeconds,
        string expectedElapsed,
        string expectedHint)
    {
        var viewModel = new UpdaterViewModel(Strings, "1.8.3", "1.9.0");
        viewModel.ShowInstalling();
        viewModel.ReportInstallerProgress(new InstallerProgressSnapshot(InstallerProgressStage.Installing, percent));

        viewModel.UpdateElapsed(TimeSpan.FromSeconds(elapsedSeconds));

        Assert.Equal(expectedElapsed, viewModel.ElapsedText);
        Assert.Equal(expectedHint, viewModel.HintText);
    }

    [Fact]
    public void Preparing_and_finishing_progress_update_detail_text()
    {
        var viewModel = new UpdaterViewModel(Strings, "1.8.3", "1.9.0");
        viewModel.ShowInstalling();

        viewModel.ReportInstallerProgress(new InstallerProgressSnapshot(InstallerProgressStage.Preparing, 0));
        Assert.Equal("正在检查安装环境", viewModel.StageDetail);
        Assert.True(viewModel.IsIndeterminate);
        viewModel.UpdateElapsed(TimeSpan.FromSeconds(5));
        Assert.Equal("正在估算剩余时间", viewModel.HintText);

        viewModel.ReportInstallerProgress(new InstallerProgressSnapshot(InstallerProgressStage.Finishing, 100));
        viewModel.UpdateElapsed(TimeSpan.FromSeconds(9));
        Assert.Equal("正在完成安装", viewModel.StageDetail);
        Assert.Equal("100%", viewModel.PercentText);
        Assert.Equal(string.Empty, viewModel.HintText);
    }

    [Fact]
    public void Completed_stage_marks_install_done_and_announces_new_version()
    {
        var viewModel = new UpdaterViewModel(Strings, "1.8.3", "1.9.0");
        viewModel.ShowInstalling();

        viewModel.ShowCompleted(TimeSpan.FromSeconds(41));

        Assert.True(viewModel.IsCompleted);
        Assert.True(viewModel.CanClose);
        Assert.Equal("更新完成", viewModel.Title);
        Assert.Equal("新版本已安装", viewModel.StageTitle);
        Assert.Equal("正在打开 HB POS 1.9.0…", viewModel.StageDetail);
        Assert.Equal("100%", viewModel.PercentText);
        Assert.Equal("总用时 00:41", viewModel.ElapsedText);
        Assert.Equal("本窗口将自动关闭", viewModel.HintText);
        Assert.Equal(UpdaterStepState.Done, viewModel.InstallStep.State);
        Assert.Equal(UpdaterStepState.Active, viewModel.LaunchStep.State);
    }

    [Fact]
    public void Failed_stage_explains_that_current_version_is_kept()
    {
        var viewModel = new UpdaterViewModel(Strings, "1.8.3", "1.9.0");

        viewModel.ShowFailed(UpdaterFailureKind.InstallerFailed, "5", canViewLog: true);

        Assert.True(viewModel.IsFailed);
        Assert.False(viewModel.IsProgressVisible);
        Assert.True(viewModel.CanClose);
        Assert.Equal("更新未完成", viewModel.Title);
        Assert.Equal("当前版本 1.8.3 保持不变，收银数据不受影响", viewModel.Subtitle);
        Assert.Equal("新版本 1.9.0 没有安装成功", viewModel.FailureTitle);
        Assert.StartsWith("安装程序返回错误代码 5。", viewModel.FailureMessage);
        Assert.True(viewModel.CanViewLog);
    }

    [Fact]
    public void Missing_versions_fall_back_to_generic_copy()
    {
        var viewModel = new UpdaterViewModel(UpdaterStrings.English, null, null);

        viewModel.ShowFailed(UpdaterFailureKind.LaunchFailed, "access denied", canViewLog: false);

        Assert.False(viewModel.HasVersions);
        Assert.Equal("The current version is unchanged and sales data is safe.", viewModel.Subtitle);
        Assert.Equal("The new version was not installed", viewModel.FailureTitle);
        Assert.Equal("Could not start the installer: access denied", viewModel.FailureMessage);

        viewModel.ShowCompleted(TimeSpan.Zero);
        Assert.Equal("Opening HB POS…", viewModel.StageDetail);
    }

    [Fact]
    public void Retrying_from_failure_resets_header_and_log_link()
    {
        var viewModel = new UpdaterViewModel(Strings, "1.8.3", "1.9.0");
        viewModel.ShowFailed(UpdaterFailureKind.InstallerFailed, "4", canViewLog: true);

        viewModel.ShowInstalling();

        Assert.Equal("正在更新 HB POS", viewModel.Title);
        Assert.False(viewModel.CanViewLog);
        Assert.True(viewModel.IsProgressVisible);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59.9, "00:59")]
    [InlineData(61, "01:01")]
    [InlineData(3600, "60:00")]
    public void FormatDuration_uses_minutes_and_seconds(double seconds, string expected)
    {
        Assert.Equal(expected, UpdaterViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
