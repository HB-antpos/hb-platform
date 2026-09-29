using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
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
    public async Task ConfirmOptionalDownloadAndInstallAsync_hides_startup_screen_only_while_dialog_is_open()
    {
        var startupScreen = new RecordingStartupScreen();
        var suspendedWhileShowing = false;
        var presenter = new CapturingDialogPresenter(true, () => suspendedWhileShowing = startupScreen.IsSuspended);
        var service = new WpfAppUpdatePromptService(null, presenter, startupScreen);

        var accepted = await service.ConfirmOptionalDownloadAndInstallAsync(new AppUpdateCheckResponse
        {
            CurrentVersion = "1.0.0",
            TargetVersion = "1.1.0"
        });

        Assert.True(accepted);
        // 启动页置顶且不在主线程上，弹框期间必须让位，关闭后恢复。
        Assert.True(suspendedWhileShowing);
        Assert.False(startupScreen.IsSuspended);
        Assert.Equal(1, startupScreen.SuspendCount);
    }

    [Fact]
    public async Task Dialog_owner_requires_a_created_window_handle()
    {
        Assert.False(WpfAppUpdatePromptDialogPresenter.CanOwnDialog(null));

        await RunOnStaDispatcherAsync(() =>
        {
            // 启动阶段 Application.MainWindow 是已构造、未显示的主窗口，直接当 owner 会抛异常。
            var window = new Window();
            Assert.False(WpfAppUpdatePromptDialogPresenter.CanOwnDialog(window));

            new WindowInteropHelper(window).EnsureHandle();
            Assert.True(WpfAppUpdatePromptDialogPresenter.CanOwnDialog(window));
            window.Close();
        });
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

    private sealed class CapturingDialogPresenter(bool? result, Action? onShow = null) : IAppUpdatePromptDialogPresenter
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
            onShow?.Invoke();
            return result;
        }
    }

    private sealed class RecordingStartupScreen : IStartupScreen
    {
        private int _depth;

        public int SuspendCount { get; private set; }

        public bool IsSuspended => _depth > 0;

        public IDisposable SuspendForDialog()
        {
            SuspendCount++;
            _depth++;
            return new Resume(this);
        }

        private sealed class Resume(RecordingStartupScreen owner) : IDisposable
        {
            public void Dispose() => owner._depth--;
        }
    }

    private static async Task RunOnStaDispatcherAsync(Action action)
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                dispatcherReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Hbpos.Client.Tests.AppUpdatePromptOwnerDispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = await dispatcherReady.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        try
        {
            await dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
        }
        finally
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout), "WPF Dispatcher thread did not shut down.");
        }
    }
}
