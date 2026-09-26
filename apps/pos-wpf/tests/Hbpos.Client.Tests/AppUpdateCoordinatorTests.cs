using System.Reflection;
using System.Xml.Linq;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.AppUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Tests;

// 会修改进程级 HBPOS_WPF_APP_VERSION / HBPOS_APP_UPDATE_CHANNEL；并行时其他走完整 DI 的用例会读到被改写的值。
[Collection(EnvironmentVariableTestCollection.Name)]
public sealed class AppUpdateCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void WpfProject_declares_release_version_source_for_app_update()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Hbpos.Client.Wpf.csproj");
        var document = XDocument.Load(projectPath);
        var properties = document.Descendants().ToList();

        Assert.Contains(
            properties,
            element => element.Name.LocalName == "HbposWpfAppVersion" &&
                element.Value.Contains("HBPOS_WPF_APP_VERSION", StringComparison.Ordinal));
        Assert.Contains(
            properties,
            element => element.Name.LocalName == "Version" &&
                element.Value.Contains("$(HbposWpfAppVersion)", StringComparison.Ordinal));
        Assert.Contains(
            properties,
            element => element.Name.LocalName == "InformationalVersion" &&
                element.Value.Contains("$(HbposWpfAppVersion)", StringComparison.Ordinal));
        Assert.DoesNotContain(
            properties,
            element => element.Name.LocalName == "Version" &&
                string.Equals(element.Value.Trim(), "1.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void AppVersionProvider_uses_environment_override_and_strips_build_metadata()
    {
        var previous = Environment.GetEnvironmentVariable(AppVersionProvider.VersionOverrideEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppVersionProvider.VersionOverrideEnvironmentVariable, " v2.3.4-preview+build.7 ");

            var provider = new AppVersionProvider();

            Assert.Equal("2.3.4", provider.CurrentVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppVersionProvider.VersionOverrideEnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData("v1.2.3+sha.abcdef", "1.2.3")]
    [InlineData("1.2.3.4-preview", "1.2.3.4")]
    [InlineData("internal-preview", "internal-preview")]
    public void AppVersionProvider_normalizes_semantic_versions_and_preserves_custom_values(
        string input,
        string expected)
    {
        Assert.Equal(expected, AppVersionProvider.NormalizeVersionText(input));
    }

    [Fact]
    public void AppUpdateChannelProvider_uses_env_then_config_then_default()
    {
        var previous = Environment.GetEnvironmentVariable(AppUpdateChannelProvider.ChannelEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppUpdateChannelProvider.ChannelEnvironmentVariable, null);
            var configured = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AppUpdate:Channel"] = " Preview "
                })
                .Build();
            Assert.Equal("preview", new AppUpdateChannelProvider(configured).CurrentChannel);

            Environment.SetEnvironmentVariable(AppUpdateChannelProvider.ChannelEnvironmentVariable, " hotfix ");
            Assert.Equal("hotfix", new AppUpdateChannelProvider(configured).CurrentChannel);

            Environment.SetEnvironmentVariable(AppUpdateChannelProvider.ChannelEnvironmentVariable, null);
            Assert.Equal("production", new AppUpdateChannelProvider(new ConfigurationBuilder().Build()).CurrentChannel);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppUpdateChannelProvider.ChannelEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void AppUpdateState_initializes_current_version_from_registered_provider()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, null, null));
        services.AddSingleton<IAppVersionProvider>(new StaticVersionProvider("2.3.4"));

        using var provider = services.BuildServiceProvider();
        var state = provider.GetRequiredService<AppUpdateState>();

        Assert.Equal("2.3.4", state.CurrentVersion);
        Assert.False(state.HasDifferentTargetVersion);
        Assert.False(state.IsRollbackTarget);
    }

    [Fact]
    public void Client_services_register_background_update_check_scheduler_with_default_interval()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, null, null));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AppUpdateBackgroundCheckOptions>();

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(AppUpdateBackgroundCheckScheduler) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.True(options.IsEnabled);
        Assert.Equal(AppUpdateBackgroundCheckOptions.DefaultInterval, options.Interval);
    }

    [Theory]
    [InlineData("1.0.0", false, false, false)]
    [InlineData("1.1.0", true, false, true)]
    [InlineData("0.9.0", true, true, true)]
    public void AppUpdateState_applies_equal_upgrade_and_rollback_versions(
        string targetVersion,
        bool updateAvailable,
        bool isRollback,
        bool expectedDifferent)
    {
        var state = new AppUpdateState();
        state.InitializeCurrentVersion("1.0.0");

        state.ApplyVersionCheck(new AppUpdateCheckResponse
        {
            CurrentVersion = "1.0.0",
            TargetVersion = targetVersion,
            UpdateAvailable = updateAvailable,
            IsRollback = isRollback
        });

        Assert.Equal("1.0.0", state.CurrentVersion);
        Assert.Equal(expectedDifferent, state.HasDifferentTargetVersion);
        Assert.Equal(expectedDifferent && isRollback, state.IsRollbackTarget);
    }

    [Fact]
    public void AppUpdateState_repeated_check_and_failure_clear_previous_target_display()
    {
        var state = new AppUpdateState();
        state.InitializeCurrentVersion("1.0.0");
        state.ApplyVersionCheck(CreateRelease(force: false));

        state.ApplyVersionCheck(AppUpdateCheckResponse.NoUpdate("1.0.0"));
        Assert.False(state.HasDifferentTargetVersion);

        state.ApplyVersionCheck(CreateRelease(force: false));
        state.ClearVersionCheckResult();

        Assert.Equal("1.0.0", state.CurrentVersion);
        Assert.False(state.HasDifferentTargetVersion);
        Assert.False(state.IsRollbackTarget);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_downloads_optional_update_before_install_confirmation()
    {
        var release = CreateRelease(force: false);
        var state = new AppUpdateState();
        var events = new List<string>();
        var prompt = new CapturingPromptService(confirm: true, events);
        var installer = new CapturingInstallerLauncher();
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"), events);
        var coordinator = CreateCoordinator(release, download, installer, prompt, state);

        var result = await coordinator.CheckForUpdatesAsync(manual: false);

        Assert.Equal(AppUpdateCoordinatorStatus.Installed, result.Status);
        Assert.Equal(["download", "prompt"], events);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.False(state.IsOptionalUpdateReady);
        Assert.False(state.IsInstallerReady);
        Assert.False(state.InstallUpdateCommand.CanExecute(null));
        Assert.True(prompt.OptionalPromptShown);
        Assert.Equal(1, download.CallCount);
        Assert.Equal("1.1.0", prompt.Update?.TargetVersion);
        Assert.Equal(@"C:\Temp\hbpos.exe", installer.FilePath);
        Assert.Equal(1, installer.LaunchCallCount);
        Assert.False(installer.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_optional_decline_after_download_does_not_install()
    {
        var release = CreateRelease(force: false);
        var state = new AppUpdateState();
        var prompt = new CapturingPromptService(confirm: false);
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var installer = new CapturingInstallerLauncher();
        var coordinator = CreateCoordinator(release, download, installer, prompt, state);

        var result = await coordinator.CheckForUpdatesAsync(manual: true);

        Assert.Equal(AppUpdateCoordinatorStatus.OptionalDeclined, result.Status);
        Assert.Equal("settings.status.appUpdateOptionalDeclined", result.StatusKey);
        Assert.True(prompt.OptionalPromptShown);
        Assert.Equal(1, download.CallCount);
        Assert.Equal(0, installer.LaunchCallCount);
        Assert.False(state.IsOptionalUpdateReady);
        Assert.Equal("1.0.0", state.CurrentVersion);
        Assert.True(state.HasDifferentTargetVersion);
        Assert.False(state.IsRollbackTarget);
        Assert.Equal("1.1.0", state.TargetVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_force_update_blocks_shell_until_installer_launch()
    {
        var release = CreateRelease(force: true);
        var state = new AppUpdateState();
        var installer = new CapturingInstallerLauncher();
        var coordinator = CreateCoordinator(
            release,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            new CapturingPromptService(),
            state);

        var result = await coordinator.CheckForUpdatesAsync(manual: false);

        Assert.Equal(AppUpdateCoordinatorStatus.ForceReady, result.Status);
        Assert.True(state.IsForceUpdateBlocking);
        Assert.True(state.IsInstallerReady);
        Assert.False(state.IsForceUpdateError);
        Assert.Equal("1.1.0", state.TargetVersion);

        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Temp\hbpos.exe", installer.FilePath);
        Assert.Equal("1.1.0", installer.Update?.TargetVersion);
        Assert.False(installer.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_force_update_downloads_without_blocking_until_ready()
    {
        var release = CreateRelease(force: true);
        var state = new AppUpdateState();
        var download = new BlockingDownloadService();
        var coordinator = CreateCoordinator(
            release,
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        var checkTask = coordinator.CheckForUpdatesAsync(manual: false);
        await download.WaitUntilStartedAsync();

        Assert.False(state.IsForceUpdateBlocking);
        Assert.True(state.IsDownloading);
        Assert.Equal("appUpdate.force.downloading", state.StatusKey);

        download.Complete(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var result = await checkTask;

        Assert.Equal(AppUpdateCoordinatorStatus.ForceReady, result.Status);
        Assert.True(state.IsForceUpdateBlocking);
        Assert.True(state.IsInstallerReady);
    }

    [Fact]
    public async Task Force_update_can_only_be_dismissed_in_debug_builds()
    {
        var state = new AppUpdateState();
        var coordinator = CreateCoordinator(
            CreateRelease(force: true),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        await coordinator.CheckForUpdatesAsync(manual: false);

#if DEBUG
        var startupResumed = false;
        state.ConfigureDebugForceUpdateDismissed(() =>
        {
            startupResumed = true;
            return Task.CompletedTask;
        });
        Assert.True(state.CanDismissForceUpdateForDebug);
        Assert.True(state.DismissForceUpdateForDebugCommand.CanExecute(null));

        await state.DismissForceUpdateForDebugCommand.ExecuteAsync(null);

        Assert.False(state.IsForceUpdateBlocking);
        Assert.False(state.IsInstallerReady);
        Assert.Null(state.TargetVersion);
        Assert.Equal(string.Empty, state.StatusKey);
        Assert.True(startupResumed);

        var pendingState = new AppUpdateState();
        var pendingCoordinator = CreateCoordinator(
            CreateRelease(force: true),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            pendingState,
            guard: new ToggleInstallSafetyGuard(canInstall: false));
        await pendingCoordinator.CheckForUpdatesAsync(manual: false);

        Assert.True(pendingState.IsForceUpdatePendingInstall);
        Assert.True(pendingState.DismissForceUpdateForDebugCommand.CanExecute(null));

        await pendingState.DismissForceUpdateForDebugCommand.ExecuteAsync(null);

        Assert.False(pendingState.IsForceUpdateRequired);
        Assert.False(pendingState.IsForceUpdatePendingInstall);
#else
        Assert.False(state.CanDismissForceUpdateForDebug);
        Assert.False(state.DismissForceUpdateForDebugCommand.CanExecute(null));
#endif
    }

    [Fact]
    public async Task CheckForUpdatesAsync_force_update_with_active_transaction_stays_nonblocking_until_safe()
    {
        var release = CreateRelease(force: true);
        var state = new AppUpdateState();
        var installer = new CapturingInstallerLauncher();
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var guard = new ToggleInstallSafetyGuard(canInstall: false);
        var coordinator = CreateCoordinator(
            release,
            download,
            installer,
            new CapturingPromptService(),
            state,
            guard: guard);

        var result = await coordinator.CheckForUpdatesAsync(manual: false);

        Assert.Equal(AppUpdateCoordinatorStatus.ForcePendingInstall, result.Status);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.True(state.IsForceUpdatePendingInstall);
        Assert.True(state.InstallUpdateCommand.CanExecute(null));
        Assert.Equal("appUpdate.install.activeTransaction", state.StatusKey);
        Assert.True(state.IsInstallerReady);
        Assert.Equal(1, download.CallCount);
        Assert.Equal(0, installer.LaunchCallCount);

        guard.CanInstall = true;
        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Temp\hbpos.exe", installer.FilePath);
        Assert.Equal(1, installer.LaunchCallCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CheckForUpdatesAsync_download_failure_shows_no_update_prompt(bool force, bool manual)
    {
        var state = new AppUpdateState();
        var prompt = new CapturingPromptService(confirm: true);
        var exitService = new CapturingApplicationExitService();
        var download = new StaticDownloadService(AppUpdateDownloadResult.Fail(null, "network failed"));
        var coordinator = CreateCoordinator(
            CreateRelease(force),
            download,
            new CapturingInstallerLauncher(),
            prompt,
            state,
            exitService,
            guard: new ToggleInstallSafetyGuard(canInstall: false));

        var result = await coordinator.CheckForUpdatesAsync(manual);

        // 中文注释：没有安装包就不能出现任何更新提示：无阻断遮罩、无待安装横幅、无确认弹窗、底部无新版本号。
        Assert.Equal(AppUpdateCoordinatorStatus.DownloadFailed, result.Status);
        Assert.Equal("settings.status.appUpdateDownloadFailed", result.StatusKey);
        Assert.Equal(1, download.CallCount);
        Assert.False(prompt.OptionalPromptShown);
        Assert.False(state.IsForceUpdateRequired);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.False(state.IsForceUpdateError);
        Assert.False(state.IsForceUpdatePendingInstall);
        Assert.False(state.IsOptionalUpdateReady);
        Assert.False(state.IsInstallerReady);
        Assert.False(state.IsDownloading);
        Assert.False(state.HasDifferentTargetVersion);
        Assert.False(state.InstallUpdateCommand.CanExecute(null));
        Assert.False(state.RetryForceUpdateCommand.CanExecute(null));
        Assert.False(state.ExitApplicationCommand.CanExecute(null));
        Assert.Equal(manual ? "settings.status.appUpdateDownloadFailed" : string.Empty, state.StatusKey);
        Assert.Equal(0, exitService.ExitCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckForUpdatesAsync_shows_target_version_only_after_package_downloaded(bool force)
    {
        var state = new AppUpdateState();
        var download = new BlockingDownloadService();
        var prompt = new CapturingPromptService(confirm: false);
        var coordinator = CreateCoordinator(
            CreateRelease(force),
            download,
            new CapturingInstallerLauncher(),
            prompt,
            state);

        var checkTask = coordinator.CheckForUpdatesAsync(manual: true);
        await download.WaitUntilStartedAsync();

        Assert.False(state.HasDifferentTargetVersion);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.False(prompt.OptionalPromptShown);

        download.Complete(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        await checkTask;

        Assert.True(state.HasDifferentTargetVersion);
        Assert.Equal("1.1.0", state.TargetVersion);
        Assert.Equal(!force, prompt.OptionalPromptShown);
        Assert.Equal(force, state.IsForceUpdateBlocking);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_force_redownload_hides_previous_target_until_new_package_ready()
    {
        var state = new AppUpdateState();
        var download = new BlockingDownloadService();
        var coordinator = CreateCoordinator(
            CreateRelease(force: true) with { TargetVersion = "1.2.0" },
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);
        state.ApplyVersionCheck(CreateRelease(force: false));
        Assert.True(state.HasDifferentTargetVersion);

        var checkTask = coordinator.CheckForUpdatesAsync(manual: false);
        await download.WaitUntilStartedAsync();

        // 中文注释：下载期间既不能沿用旧目标版本，也不能提前显示新目标版本。
        Assert.False(state.HasDifferentTargetVersion);

        download.Complete(AppUpdateDownloadResult.Fail(null, "network failed"));
        var result = await checkTask;

        Assert.Equal(AppUpdateCoordinatorStatus.DownloadFailed, result.Status);
        Assert.False(state.HasDifferentTargetVersion);
        Assert.False(state.IsForceUpdateBlocking);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_force_install_command_ignores_cancelled_check_token()
    {
        var release = CreateRelease(force: true);
        var state = new AppUpdateState();
        var installer = new CapturingInstallerLauncher();
        using var cts = new CancellationTokenSource();
        var coordinator = CreateCoordinator(
            release,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            new CapturingPromptService(),
            state);

        await coordinator.CheckForUpdatesAsync(manual: false, cts.Token);
        await cts.CancelAsync();
        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Temp\hbpos.exe", installer.FilePath);
        Assert.False(installer.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_force_install_failure_allows_retry_and_exit()
    {
        var release = CreateRelease(force: true);
        var state = new AppUpdateState();
        var exitService = new CapturingApplicationExitService();
        var installer = new SequenceInstallerLauncher(ProcessLaunchResult.Fail("launch failed"));
        var coordinator = CreateCoordinator(
            release,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            new CapturingPromptService(),
            state,
            exitService);

        await coordinator.CheckForUpdatesAsync(manual: false);
        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.True(state.IsForceUpdateBlocking);
        Assert.True(state.IsForceUpdateError);
        Assert.False(state.InstallUpdateCommand.CanExecute(null));
        Assert.True(state.RetryForceUpdateCommand.CanExecute(null));
        Assert.True(state.ExitApplicationCommand.CanExecute(null));
        Assert.Equal("appUpdate.force.downloadFailed", state.StatusKey);
        Assert.Equal(1, installer.LaunchCallCount);

        state.ExitApplicationCommand.Execute(null);
        Assert.Equal(1, exitService.ExitCallCount);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_force_download_failure_is_retried_by_next_check()
    {
        var release = CreateRelease(force: true);
        var state = new AppUpdateState();
        var failedCheck = CreateCoordinator(
            release,
            new StaticDownloadService(AppUpdateDownloadResult.Fail(null, "network failed")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        await failedCheck.CheckForUpdatesAsync(manual: false);

        Assert.False(state.IsForceUpdateBlocking);

        // 中文注释：下次启动或设置页手动检查重新下载，下载完成后才进入强更就绪遮罩。
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var nextCheck = CreateCoordinator(
            release,
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        var result = await nextCheck.CheckForUpdatesAsync(manual: true);

        Assert.Equal(AppUpdateCoordinatorStatus.ForceReady, result.Status);
        Assert.True(state.IsForceUpdateBlocking);
        Assert.True(state.IsInstallerReady);
        Assert.True(state.HasDifferentTargetVersion);
        Assert.Equal(1, download.CallCount);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_force_update_updates_download_progress_state()
    {
        var release = CreateRelease(force: true);
        var state = new AppUpdateState();
        var download = new ProgressReportingDownloadService(
            AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"),
            new AppUpdateDownloadProgress(12, 12, 100));
        var coordinator = CreateCoordinator(
            release,
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        await coordinator.CheckForUpdatesAsync(manual: false);

        Assert.True(state.HasDownloadProgress);
        Assert.Equal(100, state.DownloadProgressPercent);
        Assert.Equal("12 / 12 bytes (100%)", state.DownloadProgressText);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_concurrent_manual_check_returns_already_running_without_second_download()
    {
        var release = CreateRelease(force: true);
        var download = new BlockingDownloadService();
        var coordinator = CreateCoordinator(
            release,
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            new AppUpdateState());

        var first = coordinator.CheckForUpdatesAsync(manual: false);
        await download.WaitUntilStartedAsync();
        var second = await coordinator.CheckForUpdatesAsync(manual: true);
        download.Complete(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        await first;

        Assert.Equal(AppUpdateCoordinatorStatus.AlreadyRunning, second.Status);
        Assert.Equal("settings.status.appUpdateAlreadyRunning", second.StatusKey);
        Assert.Equal(1, download.CallCount);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_manual_no_update_returns_localized_status()
    {
        var state = new AppUpdateState();
        var coordinator = CreateCoordinator(
            AppUpdateCheckResponse.NoUpdate("1.0.0"),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        var result = await coordinator.CheckForUpdatesAsync(manual: true);

        Assert.Equal(AppUpdateCoordinatorStatus.NoUpdate, result.Status);
        Assert.Equal("settings.status.appUpdateLatest", result.StatusKey);
        Assert.Equal("1.0.0", state.CurrentVersion);
        Assert.False(state.HasDifferentTargetVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_uses_configured_channel()
    {
        var apiClient = new StaticUpdateApiClient(AppUpdateCheckResponse.NoUpdate("1.0.0"));
        var coordinator = CreateCoordinator(
            apiClient,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            new AppUpdateState(),
            channelProvider: new StaticChannelProvider("preview"));

        await coordinator.CheckForUpdatesAsync(manual: true);

        Assert.Equal("preview", apiClient.LastRequest?.Channel);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_policy_error_does_not_block_shell()
    {
        var response = AppUpdateCheckResponse.Failed(
            "1.0.0",
            "TARGET_RELEASE_NOT_FOUND",
            "Target release is disabled.");
        var state = new AppUpdateState();
        var coordinator = CreateCoordinator(
            response,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        var result = await coordinator.CheckForUpdatesAsync(manual: true);

        Assert.Equal(AppUpdateCoordinatorStatus.PolicyFailed, result.Status);
        Assert.Equal("settings.status.appUpdatePolicyFailed", result.StatusKey);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.False(state.IsInstallerReady);
        Assert.Equal("settings.status.appUpdatePolicyFailed", state.StatusKey);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_transport_check_failure_preserves_check_failed_status()
    {
        var response = AppUpdateCheckResponse.Failed(
            "1.0.0",
            "APP_UPDATE_CENTER_HTTP_ERROR",
            "center unavailable");
        var state = new AppUpdateState();
        var coordinator = CreateCoordinator(
            response,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);
        state.ApplyVersionCheck(CreateRelease(force: false));

        var result = await coordinator.CheckForUpdatesAsync(manual: true);

        Assert.Equal(AppUpdateCoordinatorStatus.CheckFailed, result.Status);
        Assert.Equal("settings.status.appUpdateCheckFailed", result.StatusKey);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.False(state.IsInstallerReady);
        Assert.Equal("settings.status.appUpdateCheckFailed", state.StatusKey);
        Assert.Equal("1.0.0", state.CurrentVersion);
        Assert.False(state.HasDifferentTargetVersion);
        Assert.False(state.IsRollbackTarget);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_unconfigured_center_preserves_error_code_for_startup_policy()
    {
        var response = AppUpdateCheckResponse.Failed(
            "1.0.0",
            "APP_UPDATE_CENTER_NOT_CONFIGURED",
            "App update center base URL is not configured.");
        var coordinator = CreateCoordinator(
            response,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            new AppUpdateState());

        var result = await coordinator.CheckForUpdatesAsync(manual: false);

        Assert.Equal(AppUpdateCoordinatorStatus.CheckFailed, result.Status);
        Assert.Equal("APP_UPDATE_CENTER_NOT_CONFIGURED", result.ErrorCode);
        Assert.Equal("App update center base URL is not configured.", result.ErrorMessage);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_optional_install_retry_success_clears_ready_state()
    {
        var release = CreateRelease(force: false);
        var state = new AppUpdateState();
        var prompt = new CapturingPromptService(confirm: true);
        var installer = new SequenceInstallerLauncher(
            ProcessLaunchResult.Fail("launch failed"),
            ProcessLaunchResult.Succeeded());
        var coordinator = CreateCoordinator(
            release,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            prompt,
            state);

        var firstResult = await coordinator.CheckForUpdatesAsync(manual: false);

        Assert.Equal(AppUpdateCoordinatorStatus.InstallFailed, firstResult.Status);
        Assert.Equal("settings.status.appUpdateInstallFailed", firstResult.StatusKey);
        Assert.Equal("launch failed", Assert.Single(firstResult.StatusArgs));
        Assert.True(state.IsOptionalUpdateReady);
        Assert.True(state.IsInstallerReady);
        Assert.True(state.InstallUpdateCommand.CanExecute(null));
        Assert.Equal(1, installer.LaunchCallCount);

        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.False(state.IsOptionalUpdateReady);
        Assert.False(state.IsInstallerReady);
        Assert.False(state.InstallUpdateCommand.CanExecute(null));
        Assert.Equal(2, installer.LaunchCallCount);
    }

    [Theory]
    [InlineData(true, null, "appUpdate.force.ready")]
    [InlineData(true, "1.0.0", "appUpdate.force.minimumRequired")]
    [InlineData(false, null, "appUpdate.force.rollbackReady")]
    public async Task CheckForUpdatesAsync_force_ready_uses_policy_specific_message(
        bool currentIsOlder,
        string? minimumVersion,
        string expectedStatusKey)
    {
        var release = CreateRelease(force: true) with
        {
            CurrentVersion = currentIsOlder ? "1.0.0" : "1.2.0",
            TargetVersion = "1.1.0",
            IsRollback = !currentIsOlder,
            MinimumSupportedVersion = minimumVersion
        };
        var state = new AppUpdateState();
        var coordinator = CreateCoordinator(
            release,
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        await coordinator.CheckForUpdatesAsync(manual: false);

        Assert.Equal(expectedStatusKey, state.StatusKey);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_optional_update_shows_ready_banner_without_modal_prompt()
    {
        var state = new AppUpdateState();
        var prompt = new CapturingPromptService(confirm: true);
        var installer = new CapturingInstallerLauncher();
        var coordinator = CreateCoordinator(
            CreateRelease(force: false),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            prompt,
            state);

        var result = await coordinator.CheckForUpdatesInBackgroundAsync();

        // 中文注释：后台检查可能落在扫码或结算中，不能弹模态确认框或自动拉起安装器，只显示右下角可关闭的就绪提示。
        Assert.Equal(AppUpdateCoordinatorStatus.OptionalReady, result.Status);
        Assert.False(prompt.OptionalPromptShown);
        Assert.Equal(0, installer.LaunchCallCount);
        Assert.True(state.IsOptionalUpdateReady);
        Assert.True(state.IsInstallerReady);
        Assert.True(state.HasDifferentTargetVersion);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.Equal("appUpdate.optional.ready", state.StatusKey);

        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Temp\hbpos.exe", installer.FilePath);
        Assert.Equal(1, installer.LaunchCallCount);
        Assert.False(state.IsOptionalUpdateReady);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_keeps_existing_ready_banner_for_same_version()
    {
        var state = new AppUpdateState();
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var installer = new SequenceInstallerLauncher(
            ProcessLaunchResult.Fail(null, "appUpdate.install.activeTransaction"));
        var coordinator = CreateCoordinator(
            CreateRelease(force: false),
            download,
            installer,
            new CapturingPromptService(),
            state);

        await coordinator.CheckForUpdatesInBackgroundAsync();
        await state.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal("appUpdate.install.activeTransaction", state.StatusKey);

        var result = await coordinator.CheckForUpdatesInBackgroundAsync();

        // 中文注释：同版本就绪提示还在时，后台检查不能重复下载，也不能把“先完成交易”的提示冲掉。
        Assert.Equal(AppUpdateCoordinatorStatus.OptionalReady, result.Status);
        Assert.Equal(1, download.CallCount);
        Assert.True(state.IsOptionalUpdateReady);
        Assert.Equal("appUpdate.install.activeTransaction", state.StatusKey);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_skips_optional_version_declined_in_prompt()
    {
        var state = new AppUpdateState();
        var prompt = new CapturingPromptService(confirm: false);
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var coordinator = CreateCoordinator(
            CreateRelease(force: false),
            download,
            new CapturingInstallerLauncher(),
            prompt,
            state);

        await coordinator.CheckForUpdatesAsync(manual: false);
        var result = await coordinator.CheckForUpdatesInBackgroundAsync();

        Assert.Equal(AppUpdateCoordinatorStatus.OptionalDeclined, result.Status);
        Assert.Equal(1, download.CallCount);
        Assert.False(state.IsOptionalUpdateReady);
        Assert.True(state.HasDifferentTargetVersion);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_skips_dismissed_optional_version_but_shows_newer_version()
    {
        var state = new AppUpdateState();
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var coordinator = CreateCoordinator(
            CreateRelease(force: false),
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        await coordinator.CheckForUpdatesInBackgroundAsync();
        state.DismissOptionalUpdateCommand.Execute(null);
        var repeated = await coordinator.CheckForUpdatesInBackgroundAsync();

        Assert.Equal(AppUpdateCoordinatorStatus.OptionalDeclined, repeated.Status);
        Assert.Equal(1, download.CallCount);
        Assert.False(state.IsOptionalUpdateReady);

        var newerDownload = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos-1.2.0.exe"));
        var newerCoordinator = CreateCoordinator(
            CreateRelease(force: false) with { TargetVersion = "1.2.0" },
            newerDownload,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);

        var newer = await newerCoordinator.CheckForUpdatesInBackgroundAsync();

        Assert.Equal(AppUpdateCoordinatorStatus.OptionalReady, newer.Status);
        Assert.Equal(1, newerDownload.CallCount);
        Assert.True(state.IsOptionalUpdateReady);
        Assert.Equal("1.2.0", state.TargetVersion);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_force_update_stays_nonblocking_even_when_safe_to_install()
    {
        var state = new AppUpdateState();
        var installer = new CapturingInstallerLauncher();
        var coordinator = CreateCoordinator(
            CreateRelease(force: true),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            new CapturingPromptService(),
            state,
            guard: new ToggleInstallSafetyGuard(canInstall: true));

        var result = await coordinator.CheckForUpdatesInBackgroundAsync();

        // 中文注释：后台发现强更时只显示待安装提示，不在收银员操作中途弹出阻断遮罩。
        Assert.Equal(AppUpdateCoordinatorStatus.ForcePendingInstall, result.Status);
        Assert.Equal("appUpdate.force.backgroundReady", result.StatusKey);
        Assert.True(state.IsForceUpdatePendingInstall);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.True(state.IsInstallerReady);
        Assert.Equal("appUpdate.force.backgroundReady", state.StatusKey);
        Assert.Equal("1.1.0", Assert.Single(state.StatusArgs));
        Assert.Equal(0, installer.LaunchCallCount);

        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Temp\hbpos.exe", installer.FilePath);
        Assert.Equal(1, installer.LaunchCallCount);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_force_install_click_during_transaction_stays_pending()
    {
        var state = new AppUpdateState();
        var installer = new CapturingInstallerLauncher();
        var guard = new ToggleInstallSafetyGuard(canInstall: false);
        var coordinator = CreateCoordinator(
            CreateRelease(force: true),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            new CapturingPromptService(),
            state,
            guard: guard);

        await coordinator.CheckForUpdatesInBackgroundAsync();
        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.True(state.IsForceUpdatePendingInstall);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.Equal("appUpdate.install.activeTransaction", state.StatusKey);
        Assert.Equal(0, installer.LaunchCallCount);

        guard.CanInstall = true;
        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, installer.LaunchCallCount);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_does_not_recheck_while_force_update_pending()
    {
        var state = new AppUpdateState();
        var apiClient = new StaticUpdateApiClient(CreateRelease(force: true));
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var coordinator = CreateCoordinator(
            apiClient,
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state,
            guard: new ToggleInstallSafetyGuard(canInstall: false));

        await coordinator.CheckForUpdatesAsync(manual: true);
        Assert.True(state.IsForceUpdatePendingInstall);

        var result = await coordinator.CheckForUpdatesInBackgroundAsync();

        Assert.Equal(AppUpdateCoordinatorStatus.AlreadyRunning, result.Status);
        Assert.Equal(1, apiClient.CallCount);
        Assert.Equal(1, download.CallCount);
        Assert.True(state.IsForceUpdatePendingInstall);
        Assert.Equal("appUpdate.install.activeTransaction", state.StatusKey);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_check_failure_keeps_displayed_target_version()
    {
        var state = new AppUpdateState();
        var coordinator = CreateCoordinator(
            AppUpdateCheckResponse.Failed("1.0.0", "APP_UPDATE_CENTER_HTTP_ERROR", "center unavailable"),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            state);
        state.ApplyVersionCheck(CreateRelease(force: false));

        var result = await coordinator.CheckForUpdatesInBackgroundAsync();

        // 中文注释：后台检查的网络抖动不能清掉底部新版本号，也不能改写设置页状态文字。
        Assert.Equal(AppUpdateCoordinatorStatus.CheckFailed, result.Status);
        Assert.Equal("APP_UPDATE_CENTER_HTTP_ERROR", result.ErrorCode);
        Assert.True(state.HasDifferentTargetVersion);
        Assert.Equal(string.Empty, state.StatusKey);
    }

    [Fact]
    public async Task CheckForUpdatesInBackgroundAsync_returns_already_running_while_foreground_check_in_progress()
    {
        var download = new BlockingDownloadService();
        var coordinator = CreateCoordinator(
            CreateRelease(force: false),
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            new AppUpdateState());

        var foreground = coordinator.CheckForUpdatesAsync(manual: false);
        await download.WaitUntilStartedAsync();
        var background = await coordinator.CheckForUpdatesInBackgroundAsync();
        download.Complete(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        await foreground;

        Assert.Equal(AppUpdateCoordinatorStatus.AlreadyRunning, background.Status);
        Assert.Equal(1, download.CallCount);
    }

    [Fact]
    public async Task CheckForUpdatesAtStartupAsync_optional_update_without_cached_installer_does_not_wait_for_download()
    {
        var state = new AppUpdateState();
        var download = new BlockingDownloadService();
        var prompt = new CapturingPromptService(confirm: true);
        var installer = new CapturingInstallerLauncher();
        var coordinator = CreateCoordinator(CreateRelease(force: false), download, installer, prompt, state);

        var result = await coordinator.CheckForUpdatesAtStartupAsync();

        // 中文注释：安装包没在本地时启动闸门立即放行，下载转到启动后继续。
        Assert.Equal(AppUpdateCoordinatorStatus.DownloadDeferred, result.Status);
        Assert.True(MainWindow.ShouldContinueStartupAfterAppUpdateCheck(result));

        await download.WaitUntilStartedAsync();
        Assert.False(state.IsOptionalUpdateReady);
        Assert.False(state.HasDifferentTargetVersion);

        download.Complete(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        await coordinator.DeferredStartupDownloadTask.WaitAsync(TestTimeout);

        // 中文注释：下载完成时收银员可能已在操作，只显示右下角就绪提示，不弹模态确认框。
        Assert.True(state.IsOptionalUpdateReady);
        Assert.True(state.HasDifferentTargetVersion);
        Assert.False(prompt.OptionalPromptShown);
        Assert.Equal(0, installer.LaunchCallCount);
    }

    [Fact]
    public async Task CheckForUpdatesAtStartupAsync_force_update_without_cached_installer_blocks_after_download_when_idle()
    {
        var state = new AppUpdateState();
        var download = new BlockingDownloadService();
        var installer = new CapturingInstallerLauncher();
        var coordinator = CreateCoordinator(
            CreateRelease(force: true),
            download,
            installer,
            new CapturingPromptService(),
            state);

        var result = await coordinator.CheckForUpdatesAtStartupAsync();

        Assert.Equal(AppUpdateCoordinatorStatus.DownloadDeferred, result.Status);
        Assert.True(MainWindow.ShouldContinueStartupAfterAppUpdateCheck(result));

        await download.WaitUntilStartedAsync();
        Assert.False(state.IsForceUpdateBlocking);
        Assert.True(state.IsDownloading);

        download.Complete(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        await coordinator.DeferredStartupDownloadTask.WaitAsync(TestTimeout);

        Assert.True(state.IsForceUpdateBlocking);
        Assert.True(state.IsInstallerReady);
        Assert.Equal(0, installer.LaunchCallCount);
    }

    [Fact]
    public async Task CheckForUpdatesAtStartupAsync_force_update_during_transaction_blocks_once_transaction_ends()
    {
        var state = new AppUpdateState();
        var guard = new ToggleInstallSafetyGuard(canInstall: false);
        var installer = new GuardedInstallerLauncher(guard);
        var delays = new ControlledDelays();
        var coordinator = CreateCoordinator(
            CreateRelease(force: true),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            new CapturingPromptService(),
            state,
            guard: guard,
            delays: delays);

        Assert.Equal(
            AppUpdateCoordinatorStatus.DownloadDeferred,
            (await coordinator.CheckForUpdatesAtStartupAsync()).Status);
        await coordinator.DeferredStartupDownloadTask.WaitAsync(TestTimeout);

        Assert.True(state.IsForceUpdatePendingInstall);
        Assert.False(state.IsForceUpdateBlocking);
        Assert.Equal("appUpdate.install.activeTransaction", state.StatusKey);

        await delays.AdvanceAsync();
        Assert.True(state.IsForceUpdatePendingInstall);

        guard.CanInstall = true;
        await delays.AdvanceAsync();
        // 中文注释：第一次看到空闲还不切换，给小票打印等收尾留出时间。
        Assert.True(state.IsForceUpdatePendingInstall);

        await delays.AdvanceLastAsync();
        await coordinator.TransactionWatchTask.WaitAsync(TestTimeout);

        // 中文注释：交易结束后只切到阻断遮罩，由收银员点“安装更新”，不自动拉起安装器。
        Assert.True(state.IsForceUpdateBlocking);
        Assert.False(state.IsForceUpdatePendingInstall);
        Assert.Equal(0, installer.LaunchCallCount);

        await state.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal(1, installer.LaunchCallCount);
    }

    [Fact]
    public async Task Transaction_watch_restarts_idle_count_when_transaction_resumes()
    {
        var state = new AppUpdateState();
        var guard = new ToggleInstallSafetyGuard(canInstall: false);
        var delays = new ControlledDelays();
        var coordinator = CreateCoordinator(
            CreateRelease(force: true),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new GuardedInstallerLauncher(guard),
            new CapturingPromptService(),
            state,
            guard: guard,
            delays: delays);

        await coordinator.CheckForUpdatesAsync(manual: false);

        guard.CanInstall = true;
        await delays.AdvanceAsync();
        guard.CanInstall = false;
        await delays.AdvanceAsync();
        guard.CanInstall = true;
        await delays.AdvanceAsync();

        Assert.True(state.IsForceUpdatePendingInstall);

        await delays.AdvanceLastAsync();
        await coordinator.TransactionWatchTask.WaitAsync(TestTimeout);

        Assert.True(state.IsForceUpdateBlocking);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckForUpdatesAtStartupAsync_handles_cached_installer_inside_startup_gate(bool force)
    {
        var state = new AppUpdateState();
        var prompt = new CapturingPromptService(confirm: false);
        var download = new StaticDownloadService(
            AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"),
            cachedInstallerPath: @"C:\Temp\hbpos.exe");
        var coordinator = CreateCoordinator(
            CreateRelease(force),
            download,
            new CapturingInstallerLauncher(),
            prompt,
            state);

        var result = await coordinator.CheckForUpdatesAtStartupAsync();

        // 中文注释：安装包已在本地时和以前一样：强更在启动闸门直接阻断，可选更新弹确认框。
        Assert.Equal(
            force ? AppUpdateCoordinatorStatus.ForceReady : AppUpdateCoordinatorStatus.OptionalDeclined,
            result.Status);
        Assert.Equal(!force, MainWindow.ShouldContinueStartupAfterAppUpdateCheck(result));
        Assert.Equal(force, state.IsForceUpdateBlocking);
        Assert.Equal(!force, prompt.OptionalPromptShown);
        Assert.Equal(1, download.CallCount);
        Assert.True(coordinator.DeferredStartupDownloadTask.IsCompleted);
    }

    [Fact]
    public async Task CheckForUpdatesAtStartupAsync_deferred_download_keeps_other_checks_out_until_finished()
    {
        var download = new BlockingDownloadService();
        var coordinator = CreateCoordinator(
            CreateRelease(force: false),
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            new AppUpdateState());

        await coordinator.CheckForUpdatesAtStartupAsync();
        await download.WaitUntilStartedAsync();

        Assert.Equal(
            AppUpdateCoordinatorStatus.AlreadyRunning,
            (await coordinator.CheckForUpdatesAsync(manual: true)).Status);
        Assert.Equal(
            AppUpdateCoordinatorStatus.AlreadyRunning,
            (await coordinator.CheckForUpdatesInBackgroundAsync()).Status);

        download.Complete(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        await coordinator.DeferredStartupDownloadTask.WaitAsync(TestTimeout);

        Assert.NotEqual(
            AppUpdateCoordinatorStatus.AlreadyRunning,
            (await coordinator.CheckForUpdatesInBackgroundAsync()).Status);
        Assert.Equal(1, download.CallCount);
    }

    [Fact]
    public async Task CheckForUpdatesAtStartupAsync_no_update_returns_without_download()
    {
        var download = new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe"));
        var coordinator = CreateCoordinator(
            AppUpdateCheckResponse.NoUpdate("1.0.0"),
            download,
            new CapturingInstallerLauncher(),
            new CapturingPromptService(),
            new AppUpdateState());

        var result = await coordinator.CheckForUpdatesAtStartupAsync();

        Assert.Equal(AppUpdateCoordinatorStatus.NoUpdate, result.Status);
        Assert.Equal(0, download.CallCount);
        Assert.Equal(AppUpdateCoordinatorStatus.NoUpdate, (await coordinator.CheckForUpdatesAsync(manual: true)).Status);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Optional_install_blocked_by_transaction_prompts_again_after_transaction_ends(
        bool confirm,
        int expectedLaunches)
    {
        var state = new AppUpdateState();
        var guard = new ToggleInstallSafetyGuard(canInstall: false);
        var installer = new GuardedInstallerLauncher(guard);
        var prompt = new CapturingPromptService(confirm);
        var delays = new ControlledDelays();
        var coordinator = CreateCoordinator(
            CreateRelease(force: false),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            prompt,
            state,
            guard: guard,
            delays: delays);

        await coordinator.CheckForUpdatesInBackgroundAsync();
        await state.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal("appUpdate.install.activeTransaction", state.StatusKey);
        Assert.Equal(0, prompt.PromptCount);

        guard.CanInstall = true;
        await delays.AdvanceAsync();
        await delays.AdvanceLastAsync();
        await coordinator.TransactionWatchTask.WaitAsync(TestTimeout);

        // 中文注释：收银员点过安装但被交易挡住，交易结束后自动弹出安装确认框。
        Assert.Equal(1, prompt.PromptCount);
        Assert.Equal(expectedLaunches, installer.LaunchCallCount);
        Assert.False(state.IsOptionalUpdateReady);
        if (!confirm)
        {
            // 中文注释：选择“稍后安装”等同关闭就绪提示，后台检查不再反复提示同一版本。
            Assert.Equal(
                AppUpdateCoordinatorStatus.OptionalDeclined,
                (await coordinator.CheckForUpdatesInBackgroundAsync()).Status);
        }
    }

    [Fact]
    public async Task Transaction_watch_stops_when_optional_banner_is_dismissed()
    {
        var state = new AppUpdateState();
        var guard = new ToggleInstallSafetyGuard(canInstall: false);
        var prompt = new CapturingPromptService(confirm: true);
        var delays = new ControlledDelays();
        var coordinator = CreateCoordinator(
            CreateRelease(force: false),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            new GuardedInstallerLauncher(guard),
            prompt,
            state,
            guard: guard,
            delays: delays);

        await coordinator.CheckForUpdatesInBackgroundAsync();
        await state.InstallUpdateCommand.ExecuteAsync(null);
        state.DismissOptionalUpdateCommand.Execute(null);

        guard.CanInstall = true;
        await delays.AdvanceAsync();
        await delays.AdvanceLastAsync();
        await coordinator.TransactionWatchTask.WaitAsync(TestTimeout);

        Assert.Equal(0, prompt.PromptCount);
        Assert.False(state.IsOptionalUpdateReady);
    }

    [Fact]
    public async Task Background_force_notice_escalates_after_transaction_only_once_cashier_clicked_install()
    {
        var state = new AppUpdateState();
        var guard = new ToggleInstallSafetyGuard(canInstall: false);
        var installer = new GuardedInstallerLauncher(guard);
        var delays = new ControlledDelays();
        var coordinator = CreateCoordinator(
            CreateRelease(force: true),
            new StaticDownloadService(AppUpdateDownloadResult.Succeeded(@"C:\Temp\hbpos.exe")),
            installer,
            new CapturingPromptService(),
            state,
            guard: guard,
            delays: delays);

        await coordinator.CheckForUpdatesInBackgroundAsync();

        // 中文注释：后台发现的强更只是提示，收银员没点安装前不监视交易、不自动阻断。
        Assert.Equal("appUpdate.force.backgroundReady", state.StatusKey);
        Assert.Equal(0, delays.RequestCount);

        await state.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal("appUpdate.install.activeTransaction", state.StatusKey);
        Assert.True(state.IsForceUpdatePendingInstall);

        guard.CanInstall = true;
        await delays.AdvanceAsync();
        await delays.AdvanceLastAsync();
        await coordinator.TransactionWatchTask.WaitAsync(TestTimeout);

        Assert.True(state.IsForceUpdateBlocking);
        Assert.Equal(0, installer.LaunchCallCount);
    }

    private static AppUpdateCoordinator CreateCoordinator(
        AppUpdateCheckResponse response,
        IAppUpdateDownloadService downloadService,
        IAppUpdateInstallerLauncher installerLauncher,
        IAppUpdatePromptService promptService,
        AppUpdateState state,
        IApplicationExitService? exitService = null,
        IAppUpdateChannelProvider? channelProvider = null,
        IAppUpdateInstallSafetyGuard? guard = null,
        ControlledDelays? delays = null)
    {
        return CreateCoordinator(
            new StaticUpdateApiClient(response),
            downloadService,
            installerLauncher,
            promptService,
            state,
            exitService,
            channelProvider,
            guard,
            delays);
    }

    private static AppUpdateCoordinator CreateCoordinator(
        IAppUpdateApiClient apiClient,
        IAppUpdateDownloadService downloadService,
        IAppUpdateInstallerLauncher installerLauncher,
        IAppUpdatePromptService promptService,
        AppUpdateState state,
        IApplicationExitService? exitService = null,
        IAppUpdateChannelProvider? channelProvider = null,
        IAppUpdateInstallSafetyGuard? guard = null,
        ControlledDelays? delays = null)
    {
        var versionProvider = new StaticVersionProvider("1.0.0");
        state.InitializeCurrentVersion(versionProvider.CurrentVersion);
        return new AppUpdateCoordinator(
            versionProvider,
            apiClient,
            downloadService,
            installerLauncher,
            guard ?? AllowInstallSafetyGuard.Instance,
            promptService,
            state,
            exitService ?? new CapturingApplicationExitService(),
            channelProvider ?? new StaticChannelProvider("production"),
            // 中文注释：默认让交易结束监视永不触发，只有显式传入可控等待的用例才推进监视循环。
            delays is null
                ? (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken)
                : delays.DelayAsync);
    }

    private static AppUpdateCheckResponse CreateRelease(bool force) => new()
    {
        UpdateAvailable = true,
        ForceUpdate = force,
        CurrentVersion = "1.0.0",
        TargetVersion = "1.1.0",
        DownloadUrl = "https://downloads.example/hbpos.exe",
        FileName = "hbpos.exe",
        FileSize = 12,
        Sha256 = new string('a', 64),
        InstallerType = "exe",
        ReleaseNotes = "更新说明"
    };

    private sealed class StaticVersionProvider(string version) : IAppVersionProvider
    {
        public string CurrentVersion => version;
    }

    private sealed class StaticUpdateApiClient(AppUpdateCheckResponse response) : IAppUpdateApiClient
    {
        public AppUpdateCheckRequest? LastRequest { get; private set; }

        public int CallCount { get; private set; }

        public Task<AppUpdateCheckResponse> CheckAsync(
            AppUpdateCheckRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(response with { CurrentVersion = response.CurrentVersion });
        }
    }

    private sealed class StaticChannelProvider(string channel) : IAppUpdateChannelProvider
    {
        public string CurrentChannel => channel;
    }

    private sealed class StaticDownloadService(
        AppUpdateDownloadResult result,
        List<string>? events = null,
        string? cachedInstallerPath = null) : IAppUpdateDownloadService
    {
        public int CallCount { get; private set; }

        public Task<AppUpdateDownloadResult> DownloadAsync(
            AppUpdateCheckResponse update,
            IProgress<AppUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            events?.Add("download");
            return Task.FromResult(result);
        }

        public Task<string?> TryGetVerifiedCachedInstallerAsync(
            AppUpdateCheckResponse update,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(cachedInstallerPath);
        }
    }

    private sealed class BlockingDownloadService : IAppUpdateDownloadService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AppUpdateDownloadResult> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task WaitUntilStartedAsync() => _started.Task;

        public void Complete(AppUpdateDownloadResult result) => _completed.SetResult(result);

        public Task<AppUpdateDownloadResult> DownloadAsync(
            AppUpdateCheckResponse update,
            IProgress<AppUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            _started.TrySetResult();
            return _completed.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ProgressReportingDownloadService(
        AppUpdateDownloadResult result,
        AppUpdateDownloadProgress value) : IAppUpdateDownloadService
    {
        public Task<AppUpdateDownloadResult> DownloadAsync(
            AppUpdateCheckResponse update,
            IProgress<AppUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(value);
            return Task.FromResult(result);
        }
    }

    private sealed class CapturingInstallerLauncher : IAppUpdateInstallerLauncher
    {
        public string? FilePath { get; private set; }

        public AppUpdateCheckResponse? Update { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public int LaunchCallCount { get; private set; }

        public Task<ProcessLaunchResult> LaunchAsync(
            string installerPath,
            AppUpdateCheckResponse update,
            CancellationToken cancellationToken = default)
        {
            FilePath = installerPath;
            Update = update;
            CancellationToken = cancellationToken;
            LaunchCallCount++;
            return Task.FromResult(ProcessLaunchResult.Succeeded());
        }
    }

    // 中文注释：与真实 AppUpdateInstallerLauncher 一样先过安全守卫，被交易挡住时返回 activeTransaction。
    private sealed class GuardedInstallerLauncher(IAppUpdateInstallSafetyGuard guard) : IAppUpdateInstallerLauncher
    {
        public int LaunchCallCount { get; private set; }

        public Task<ProcessLaunchResult> LaunchAsync(
            string installerPath,
            AppUpdateCheckResponse update,
            CancellationToken cancellationToken = default)
        {
            if (!guard.CanInstallUpdate(out var statusKey, out var statusArgs))
            {
                return Task.FromResult(ProcessLaunchResult.Fail(null, statusKey, statusArgs));
            }

            LaunchCallCount++;
            return Task.FromResult(ProcessLaunchResult.Succeeded());
        }
    }

    private sealed class SequenceInstallerLauncher(params ProcessLaunchResult[] results) : IAppUpdateInstallerLauncher
    {
        private readonly Queue<ProcessLaunchResult> _results = new(results);

        public int LaunchCallCount { get; private set; }

        public Task<ProcessLaunchResult> LaunchAsync(
            string installerPath,
            AppUpdateCheckResponse update,
            CancellationToken cancellationToken = default)
        {
            LaunchCallCount++;
            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : ProcessLaunchResult.Succeeded());
        }
    }

    private sealed class CapturingPromptService(
        bool confirm = false,
        List<string>? events = null) : IAppUpdatePromptService
    {
        public bool OptionalPromptShown { get; private set; }

        public int PromptCount { get; private set; }

        public AppUpdateCheckResponse? Update { get; private set; }

        public Task<bool> ConfirmOptionalDownloadAndInstallAsync(
            AppUpdateCheckResponse update,
            CancellationToken cancellationToken = default)
        {
            OptionalPromptShown = true;
            PromptCount++;
            Update = update;
            events?.Add("prompt");
            return Task.FromResult(confirm);
        }
    }

    private sealed class ToggleInstallSafetyGuard(bool canInstall) : IAppUpdateInstallSafetyGuard
    {
        public bool CanInstall { get; set; } = canInstall;

        public bool CanInstallUpdate(out string statusKey, out object[] args)
        {
            statusKey = CanInstall ? string.Empty : "appUpdate.install.activeTransaction";
            args = [];
            return CanInstall;
        }
    }

    private sealed class CapturingApplicationExitService : IApplicationExitService
    {
        public int ExitCallCount { get; private set; }

        public void Exit()
        {
            ExitCallCount++;
        }
    }

    private static Func<Task<ProcessLaunchResult>> GetPendingInstallAsync(AppUpdateState state)
    {
        var field = typeof(AppUpdateState).GetField("_installAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var value = field?.GetValue(state);
        return Assert.IsType<Func<Task<ProcessLaunchResult>>>(value);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "hb-platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
