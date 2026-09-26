using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Tests;

public sealed class AppUpdateInstallerLauncherTests
{
    [Fact]
    public async Task LaunchAsync_starts_exe_with_configured_arguments_and_exits_app()
    {
        var processLauncher = new CapturingProcessLauncher();
        var exitService = new CapturingExitService();
        var launcher = new AppUpdateInstallerLauncher(processLauncher, exitService, AllowInstallSafetyGuard.Instance);

        var result = await launcher.LaunchAsync(
            @"C:\Temp\hbpos.exe",
            new AppUpdateCheckResponse
            {
                InstallerType = "exe",
                InstallerArguments = "/quiet /norestart"
            });

        Assert.True(result.Success);
        Assert.Equal(@"C:\Temp\hbpos.exe", processLauncher.FileName);
        Assert.Equal("/quiet /norestart", processLauncher.Arguments);
        Assert.True(exitService.Exited);
    }

    [Fact]
    public async Task LaunchAsync_starts_msi_through_msiexec_and_exits_app()
    {
        var processLauncher = new CapturingProcessLauncher();
        var exitService = new CapturingExitService();
        var launcher = new AppUpdateInstallerLauncher(processLauncher, exitService, AllowInstallSafetyGuard.Instance);

        var result = await launcher.LaunchAsync(
            @"C:\Temp\hbpos.msi",
            new AppUpdateCheckResponse
            {
                InstallerType = "msi",
                InstallerArguments = "/qn"
            });

        Assert.True(result.Success);
        Assert.Equal("msiexec.exe", processLauncher.FileName);
        Assert.Equal(@"/i ""C:\Temp\hbpos.msi"" /qn", processLauncher.Arguments);
        Assert.True(exitService.Exited);
    }

    [Fact]
    public async Task LaunchAsync_does_not_start_installer_or_exit_when_safety_guard_blocks()
    {
        var processLauncher = new CapturingProcessLauncher();
        var exitService = new CapturingExitService();
        var guard = new BlockingInstallSafetyGuard("appUpdate.install.activeTransaction");
        var launcher = new AppUpdateInstallerLauncher(processLauncher, exitService, guard);

        var result = await launcher.LaunchAsync(
            @"C:\Temp\hbpos.exe",
            new AppUpdateCheckResponse { InstallerType = "exe" });

        Assert.False(result.Success);
        Assert.Equal("appUpdate.install.activeTransaction", result.StatusKey);
        Assert.Null(processLauncher.FileName);
        Assert.False(exitService.Exited);
    }

    [Fact]
    public async Task LaunchAsync_does_not_exit_when_process_launch_fails()
    {
        var processLauncher = new CapturingProcessLauncher
        {
            Result = ProcessLaunchResult.Fail("Process.Start returned null.")
        };
        var exitService = new CapturingExitService();
        var launcher = new AppUpdateInstallerLauncher(processLauncher, exitService, AllowInstallSafetyGuard.Instance);

        var result = await launcher.LaunchAsync(
            @"C:\Temp\hbpos.exe",
            new AppUpdateCheckResponse { InstallerType = "exe" });

        Assert.False(result.Success);
        Assert.Contains("null", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(exitService.Exited);
    }

    [Fact]
    public async Task LaunchAsync_rejects_declared_type_that_does_not_match_downloaded_extension()
    {
        var processLauncher = new CapturingProcessLauncher();
        var exitService = new CapturingExitService();
        var launcher = new AppUpdateInstallerLauncher(processLauncher, exitService, AllowInstallSafetyGuard.Instance);

        var result = await launcher.LaunchAsync(
            @"C:\Temp\hbpos.msi",
            new AppUpdateCheckResponse { InstallerType = "exe" });

        Assert.False(result.Success);
        Assert.Equal("appUpdate.install.failed", result.StatusKey);
        Assert.Null(processLauncher.FileName);
        Assert.False(exitService.Exited);
    }

    [Fact]
    public async Task LaunchAsync_hands_exe_installer_to_progress_window_and_exits_app()
    {
        var processLauncher = new CapturingProcessLauncher();
        var exitService = new CapturingExitService();
        var progressWindow = new CapturingProgressWindowLauncher(ProcessLaunchResult.Succeeded());
        var launcher = new AppUpdateInstallerLauncher(processLauncher, exitService, AllowInstallSafetyGuard.Instance, progressWindow);
        var update = new AppUpdateCheckResponse
        {
            InstallerType = "exe",
            InstallerArguments = " /SP- /VERYSILENT ",
            TargetVersion = "1.9.0"
        };

        var result = await launcher.LaunchAsync(@"C:\Temp\hbpos.exe", update);

        Assert.True(result.Success);
        Assert.Equal(@"C:\Temp\hbpos.exe", progressWindow.InstallerPath);
        Assert.Equal("/SP- /VERYSILENT", progressWindow.InstallerArguments);
        Assert.Same(update, progressWindow.Update);
        Assert.Null(processLauncher.FileName);
        Assert.True(exitService.Exited);
    }

    [Fact]
    public async Task LaunchAsync_falls_back_to_installer_when_progress_window_is_unavailable()
    {
        var processLauncher = new CapturingProcessLauncher();
        var exitService = new CapturingExitService();
        var progressWindow = new CapturingProgressWindowLauncher(null);
        var launcher = new AppUpdateInstallerLauncher(processLauncher, exitService, AllowInstallSafetyGuard.Instance, progressWindow);

        var result = await launcher.LaunchAsync(
            @"C:\Temp\hbpos.exe",
            new AppUpdateCheckResponse { InstallerType = "exe", InstallerArguments = "/VERYSILENT" });

        Assert.True(result.Success);
        Assert.NotNull(progressWindow.InstallerPath);
        Assert.Equal(@"C:\Temp\hbpos.exe", processLauncher.FileName);
        Assert.Equal("/VERYSILENT", processLauncher.Arguments);
        Assert.True(exitService.Exited);
    }

    [Fact]
    public async Task LaunchAsync_falls_back_to_installer_when_progress_window_throws()
    {
        var processLauncher = new CapturingProcessLauncher();
        var exitService = new CapturingExitService();
        var progressWindow = new CapturingProgressWindowLauncher(null)
        {
            Exception = new InvalidOperationException("broken staging")
        };
        var launcher = new AppUpdateInstallerLauncher(processLauncher, exitService, AllowInstallSafetyGuard.Instance, progressWindow);

        var result = await launcher.LaunchAsync(@"C:\Temp\hbpos.exe", new AppUpdateCheckResponse { InstallerType = "exe" });

        Assert.True(result.Success);
        Assert.Equal(@"C:\Temp\hbpos.exe", processLauncher.FileName);
        Assert.True(exitService.Exited);
    }

    [Fact]
    public async Task LaunchAsync_keeps_msi_and_blocked_installs_away_from_progress_window()
    {
        var progressWindow = new CapturingProgressWindowLauncher(ProcessLaunchResult.Succeeded());
        var msiLauncher = new AppUpdateInstallerLauncher(
            new CapturingProcessLauncher(),
            new CapturingExitService(),
            AllowInstallSafetyGuard.Instance,
            progressWindow);
        var blockedLauncher = new AppUpdateInstallerLauncher(
            new CapturingProcessLauncher(),
            new CapturingExitService(),
            new BlockingInstallSafetyGuard("appUpdate.install.activeTransaction"),
            progressWindow);

        await msiLauncher.LaunchAsync(@"C:\Temp\hbpos.msi", new AppUpdateCheckResponse { InstallerType = "msi" });
        var blocked = await blockedLauncher.LaunchAsync(@"C:\Temp\hbpos.exe", new AppUpdateCheckResponse { InstallerType = "exe" });

        Assert.False(blocked.Success);
        Assert.Null(progressWindow.InstallerPath);
    }

    private sealed class CapturingProgressWindowLauncher(ProcessLaunchResult? result) : IAppUpdateProgressWindowLauncher
    {
        public string? InstallerPath { get; private set; }

        public string? InstallerArguments { get; private set; }

        public AppUpdateCheckResponse? Update { get; private set; }

        public Exception? Exception { get; init; }

        public Task<ProcessLaunchResult?> TryLaunchAsync(
            string installerPath,
            string installerArguments,
            AppUpdateCheckResponse update)
        {
            InstallerPath = installerPath;
            InstallerArguments = installerArguments;
            Update = update;
            return Exception is null
                ? Task.FromResult(result)
                : Task.FromException<ProcessLaunchResult?>(Exception);
        }
    }

    private sealed class CapturingProcessLauncher : IProcessLauncher
    {
        public string? FileName { get; private set; }

        public string? Arguments { get; private set; }

        public ProcessLaunchResult Result { get; init; } = ProcessLaunchResult.Succeeded();

        public Task<ProcessLaunchResult> StartAsync(string fileName, string arguments)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(Result);
        }
    }

    private sealed class BlockingInstallSafetyGuard(string statusKey) : IAppUpdateInstallSafetyGuard
    {
        public bool CanInstallUpdate(out string blockedStatusKey, out object[] args)
        {
            blockedStatusKey = statusKey;
            args = [];
            return false;
        }
    }

    private sealed class CapturingExitService : IApplicationExitService
    {
        public bool Exited { get; private set; }

        public void Exit()
        {
            Exited = true;
        }
    }
}
