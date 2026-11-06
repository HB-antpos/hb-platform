using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Tests;

// 中文注释：底部「最新版本」按钮的点击行为：安装包还在就安装，点过「稍后」后重新检查，交易中只提示。
public sealed class AppUpdateFooterButtonTests
{
    private static readonly AppUpdateCheckResponse Update = new()
    {
        UpdateAvailable = true,
        ForceUpdate = false,
        CurrentVersion = "1.0.102",
        TargetVersion = "1.0.108"
    };

    [Fact]
    public async Task Click_installs_when_installer_is_still_ready()
    {
        var state = CreateState(out var installs, out var rechecks);
        state.ShowOptionalUpdateReady(Update, @"C:\Temp\hbpos.exe", CountingInstall(installs));
        state.ApplyVersionCheck(Update);

        await state.UpdateFromFooterCommand.ExecuteAsync(null);

        Assert.Equal(1, installs.Count);
        Assert.Equal(0, rechecks.Count);
    }

    [Fact]
    public async Task Click_rechecks_after_the_notice_was_dismissed()
    {
        var state = CreateState(out var installs, out var rechecks);
        state.ShowOptionalUpdateReady(Update, @"C:\Temp\hbpos.exe", CountingInstall(installs));
        state.ApplyVersionCheck(Update);
        state.DismissOptionalUpdate();

        // 中文注释：「稍后」清掉了安装入口，但底部仍显示新版本号，这正是点击要补上的入口。
        Assert.True(state.HasDifferentTargetVersion);
        Assert.False(state.IsInstallerReady);

        await state.UpdateFromFooterCommand.ExecuteAsync(null);

        Assert.Equal(0, installs.Count);
        Assert.Equal(1, rechecks.Count);
        Assert.False(state.IsFooterHintVisible);
    }

    [Fact]
    public async Task Click_during_a_sale_only_shows_a_hint()
    {
        ConfigureEnglish();
        var state = CreateState(out var installs, out var rechecks);
        state.ShowOptionalUpdateReady(Update, @"C:\Temp\hbpos.exe", CountingInstall(installs));
        state.ApplyVersionCheck(Update);
        state.IsTransactionInProgress = true;

        await state.UpdateFromFooterCommand.ExecuteAsync(null);

        Assert.Equal(0, installs.Count);
        Assert.Equal(0, rechecks.Count);
        Assert.True(state.IsFooterHintVisible);
        Assert.False(string.IsNullOrWhiteSpace(state.FooterHintText));
    }

    [Fact]
    public async Task Click_during_a_sale_after_dismiss_does_not_recheck()
    {
        var state = CreateState(out _, out var rechecks);
        state.ApplyVersionCheck(Update);
        state.IsTransactionInProgress = true;

        await state.UpdateFromFooterCommand.ExecuteAsync(null);

        Assert.Equal(0, rechecks.Count);
        Assert.True(state.IsFooterHintVisible);
    }

    [Theory]
    [InlineData(AppUpdateCoordinatorStatus.CheckFailed, true)]
    [InlineData(AppUpdateCoordinatorStatus.DownloadFailed, true)]
    [InlineData(AppUpdateCoordinatorStatus.InstallFailed, true)]
    [InlineData(AppUpdateCoordinatorStatus.OptionalDeclined, false)]
    [InlineData(AppUpdateCoordinatorStatus.AlreadyRunning, false)]
    [InlineData(AppUpdateCoordinatorStatus.Installed, false)]
    public async Task Recheck_shows_a_hint_only_when_it_failed(AppUpdateCoordinatorStatus status, bool expectHint)
    {
        ConfigureEnglish();
        var state = new AppUpdateState();
        state.InitializeCurrentVersion("1.0.102");
        state.ApplyVersionCheck(Update);
        state.ConfigureFooterRecheck(() => Task.FromResult(AppUpdateCoordinatorResult.FromStatus(status)));

        await state.UpdateFromFooterCommand.ExecuteAsync(null);

        Assert.Equal(expectHint, state.IsFooterHintVisible);
    }

    [Fact]
    public async Task Click_without_a_recheck_hook_is_ignored()
    {
        var state = new AppUpdateState();
        state.InitializeCurrentVersion("1.0.102");
        state.ApplyVersionCheck(Update);

        await state.UpdateFromFooterCommand.ExecuteAsync(null);

        Assert.False(state.IsFooterHintVisible);
    }

    [Fact]
    public void Command_is_disabled_while_the_installer_downloads()
    {
        var state = CreateState(out _, out _);
        Assert.True(state.UpdateFromFooterCommand.CanExecute(null));

        state.ShowForceUpdateDownloading(new AppUpdateCheckResponse
        {
            UpdateAvailable = true,
            ForceUpdate = true,
            CurrentVersion = "1.0.102",
            TargetVersion = "1.0.108"
        });

        Assert.False(state.UpdateFromFooterCommand.CanExecute(null));
    }

    [Fact]
    public async Task Command_reports_running_while_the_recheck_is_in_flight()
    {
        var state = new AppUpdateState();
        state.InitializeCurrentVersion("1.0.102");
        state.ApplyVersionCheck(Update);
        var release = new TaskCompletionSource<AppUpdateCoordinatorResult>();
        state.ConfigureFooterRecheck(() => release.Task);

        var click = state.UpdateFromFooterCommand.ExecuteAsync(null);

        // 中文注释：检查期间按钮换成进度，并且不能重复点击。
        Assert.True(state.UpdateFromFooterCommand.IsRunning);
        Assert.False(state.UpdateFromFooterCommand.CanExecute(null));

        release.SetResult(AppUpdateCoordinatorResult.NoUpdate());
        await click;
        Assert.False(state.UpdateFromFooterCommand.IsRunning);
    }

    private static AppUpdateState CreateState(out Counter installs, out Counter rechecks)
    {
        var recheckCounter = new Counter();
        var state = new AppUpdateState();
        state.InitializeCurrentVersion("1.0.102");
        state.ConfigureFooterRecheck(() =>
        {
            recheckCounter.Count++;
            return Task.FromResult(AppUpdateCoordinatorResult.NoUpdate());
        });
        installs = new Counter();
        rechecks = recheckCounter;
        return state;
    }

    private static Func<Task<ProcessLaunchResult>> CountingInstall(Counter counter) => () =>
    {
        counter.Count++;
        return Task.FromResult(ProcessLaunchResult.Succeeded());
    };

    private static void ConfigureEnglish()
    {
        var localization = new LocalizationService();
        localization.SetCulture("en-US");
        LocalizationResourceProvider.Instance.Configure(localization);
    }

    private sealed class Counter
    {
        public int Count { get; set; }
    }
}
