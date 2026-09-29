using System.Windows;
using System.Windows.Markup;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Tests;

public sealed class AppUpdatePromptServiceTests
{
    [Fact]
    public async Task ConfirmOptionalDownloadAndInstallAsync_passes_localized_update_content_to_custom_dialog()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var presenter = new CapturingDialogPresenter(true);
        var service = new WpfAppUpdatePromptService(localization, presenter);
        var update = new AppUpdateCheckResponse
        {
            CurrentVersion = " 1.4.2 ",
            TargetVersion = "1.5.0",
            ReleaseNotes = "- 扫码稳定性改进\r\n* 查询速度优化\n\u2022 安全修复"
        };

        var accepted = await service.ConfirmOptionalDownloadAndInstallAsync(update);

        Assert.True(accepted);
        Assert.Equal(1, presenter.ShowCount);
        Assert.NotNull(presenter.ViewModel);
        Assert.Equal("1.4.2", presenter.ViewModel.CurrentVersion);
        Assert.Equal("1.5.0", presenter.ViewModel.TargetVersion);
        Assert.Equal(["扫码稳定性改进", "查询速度优化", "安全修复"], presenter.ViewModel.ReleaseNotes);
        Assert.True(presenter.ViewModel.HasReleaseNotes);
        Assert.Equal("zh-CN", presenter.Language?.IetfLanguageTag, ignoreCase: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ConfirmOptionalDownloadAndInstallAsync_treats_decline_or_window_close_as_not_confirmed(bool? dialogResult)
    {
        var presenter = new CapturingDialogPresenter(dialogResult);
        var service = new WpfAppUpdatePromptService(null, presenter);

        var accepted = await service.ConfirmOptionalDownloadAndInstallAsync(new AppUpdateCheckResponse
        {
            CurrentVersion = "1.0.0",
            TargetVersion = "1.1.0"
        });

        Assert.False(accepted);
        Assert.Equal(1, presenter.ShowCount);
    }

    [Fact]
    public async Task ConfirmOptionalDownloadAndInstallAsync_honors_pre_cancelled_token_before_showing_dialog()
    {
        var presenter = new CapturingDialogPresenter(true);
        var service = new WpfAppUpdatePromptService(null, presenter);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ConfirmOptionalDownloadAndInstallAsync(
                new AppUpdateCheckResponse(),
                cancellation.Token));

        Assert.Equal(0, presenter.ShowCount);
    }

    [Fact]
    public void Prompt_view_model_exposes_empty_release_notes_state_and_safe_version_fallbacks()
    {
        var viewModel = new AppUpdatePromptViewModel(new AppUpdateCheckResponse
        {
            CurrentVersion = " ",
            TargetVersion = string.Empty,
            ReleaseNotes = "\r\n  \n"
        });

        Assert.Equal("-", viewModel.CurrentVersion);
        Assert.Equal("-", viewModel.TargetVersion);
        Assert.False(viewModel.HasReleaseNotes);
        Assert.Empty(viewModel.ReleaseNotes);
    }

    [Fact]
    public void Prompt_placement_covers_the_visible_cashier_window()
    {
        var placement = WpfAppUpdatePromptDialogPresenter.ResolvePlacement(
            new Size(1366, 728),
            new Rect(0, 0, 1920, 1040));

        Assert.Equal(WindowStartupLocation.CenterOwner, placement.StartupLocation);
        Assert.Equal(1366, placement.Bounds.Width);
        Assert.Equal(728, placement.Bounds.Height);
    }

    [Fact]
    public void Prompt_placement_falls_back_when_cashier_window_has_no_layout_size()
    {
        var placement = WpfAppUpdatePromptDialogPresenter.ResolvePlacement(
            new Size(0, double.NaN),
            new Rect(0, 0, 1920, 1040));

        Assert.Equal(WindowStartupLocation.CenterOwner, placement.StartupLocation);
        Assert.Equal(1200, placement.Bounds.Width);
        Assert.Equal(760, placement.Bounds.Height);
    }

    [Fact]
    public void Prompt_placement_covers_work_area_during_startup_instead_of_shrinking_to_splash()
    {
        // 中文注释：启动检查时没有可见的收银主窗口（owner 只是 460×380 启动页），弹窗必须铺满工作区。
        var workArea = new Rect(0, 0, 1024, 728);

        var placement = WpfAppUpdatePromptDialogPresenter.ResolvePlacement(null, workArea);

        Assert.Equal(WindowStartupLocation.Manual, placement.StartupLocation);
        Assert.Equal(workArea, placement.Bounds);
    }

    [Fact]
    public void Prompt_placement_centers_fallback_size_when_work_area_is_unavailable()
    {
        var placement = WpfAppUpdatePromptDialogPresenter.ResolvePlacement(null, Rect.Empty);

        Assert.Equal(WindowStartupLocation.CenterScreen, placement.StartupLocation);
        Assert.Equal(1200, placement.Bounds.Width);
        Assert.Equal(760, placement.Bounds.Height);
    }

    private sealed class CapturingDialogPresenter(bool? result) : IAppUpdatePromptDialogPresenter
    {
        public int ShowCount { get; private set; }

        public AppUpdatePromptViewModel? ViewModel { get; private set; }

        public Window? Owner { get; private set; }

        public XmlLanguage? Language { get; private set; }

        public bool? Show(AppUpdatePromptViewModel viewModel, Window? owner, XmlLanguage language)
        {
            ShowCount++;
            ViewModel = viewModel;
            Owner = owner;
            Language = language;
            return result;
        }
    }
}
