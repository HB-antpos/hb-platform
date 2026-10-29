using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.AppUpdates;
using Hbpos.Updater;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class AppUpdateProgressWindowLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hbpos-updater-launch-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 26, 10, 15, 0, TimeSpan.Zero));

    public AppUpdateProgressWindowLauncherTests()
    {
        Directory.CreateDirectory(UpdaterSourceDirectory);
        File.WriteAllText(Path.Combine(UpdaterSourceDirectory, AppUpdateProgressWindowOptions.UpdaterExecutableName), "exe");
        File.WriteAllText(Path.Combine(UpdaterSourceDirectory, "Hbpos.Updater.dll"), "dll");
        Directory.CreateDirectory(Path.Combine(UpdaterSourceDirectory, "zh-CN"));
        File.WriteAllText(Path.Combine(UpdaterSourceDirectory, "zh-CN", "resources.dll"), "satellite");
    }

    private string UpdaterSourceDirectory => Path.Combine(_root, "install", "updater");

    private string StagingRoot => Path.Combine(_root, "temp", "HbposUpdater");

    private string LogDirectory => Path.Combine(_root, "logs");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TryLaunchAsync_stages_updater_outside_install_folder_and_passes_update_context()
    {
        var processLauncher = new CapturingProcessLauncher();
        var launcher = CreateLauncher(processLauncher);

        var result = await launcher.TryLaunchAsync(
            @"C:\Updates\Hbpos.Client.Wpf-1.9.0-x64.exe",
            "/SP- /VERYSILENT",
            new AppUpdateCheckResponse { TargetVersion = "v1.9.0" });

        Assert.NotNull(result);
        Assert.True(result.Success);
        var stagedExe = Assert.IsType<string>(processLauncher.FileName);
        Assert.StartsWith(StagingRoot, stagedExe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AppUpdateProgressWindowOptions.UpdaterExecutableName, Path.GetFileName(stagedExe));
        var stagedDirectory = Path.GetDirectoryName(stagedExe)!;
        Assert.True(File.Exists(Path.Combine(stagedDirectory, "Hbpos.Updater.dll")));
        Assert.True(File.Exists(Path.Combine(stagedDirectory, "zh-CN", "resources.dll")));

        Assert.True(UpdaterOptions.TryParse(WindowsArgumentParser.Parse(processLauncher.Arguments!), out var options));
        Assert.Equal(@"C:\Updates\Hbpos.Client.Wpf-1.9.0-x64.exe", options!.InstallerPath);
        Assert.Equal("/SP- /VERYSILENT", options.InstallerArguments);
        Assert.Equal(@"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe", options.AppExePath);
        Assert.Equal(4242, options.WaitProcessId);
        Assert.Equal("1.8.3", options.FromVersion);
        Assert.Equal("1.9.0", options.ToVersion);
        Assert.Equal("zh-CN", options.Culture);
        Assert.Equal(Path.Combine(LogDirectory, "install-1.9.0-20260926-101500.log"), options.LogPath);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("any", true, false)]
    [InlineData("agent-only", true, true)]
    public async Task TryLaunchAsync_passes_release_contract_for_update_agent_and_unattended_mode(
        string? mode,
        bool expectedUnattended,
        bool expectedAgentOnly)
    {
        var processLauncher = new CapturingProcessLauncher();
        var launcher = CreateLauncher(processLauncher);

        await launcher.TryLaunchAsync(
            @"C:\Updates\Hbpos.Client.Wpf-1.9.0-x64.exe",
            "/SP- /VERYSILENT",
            new AppUpdateCheckResponse
            {
                TargetVersion = "1.9.0",
                DownloadUrl = "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/production/1.9.0/setup.exe",
                Sha256 = new string('a', 64),
                FileSize = 2048
            },
            mode switch
            {
                "any" => UnattendedInstallMode.AnyInstaller,
                "agent-only" => UnattendedInstallMode.AgentOnly,
                _ => null
            });

        Assert.True(UpdaterOptions.TryParse(WindowsArgumentParser.Parse(processLauncher.Arguments!), out var options));
        Assert.Equal("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/production/1.9.0/setup.exe", options!.DownloadUrl);
        Assert.Equal(new string('a', 64), options.Sha256);
        Assert.Equal(2048, options.FileSize);
        Assert.Equal(expectedUnattended, options.Unattended);
        Assert.Equal(expectedAgentOnly, options.AgentOnly);
    }

    [Fact]
    public async Task Installer_launcher_unattended_entry_marks_update_window_unattended_and_attended_does_not()
    {
        var progressWindow = new RecordingProgressWindowLauncher();
        var launcher = new AppUpdateInstallerLauncher(
            new CapturingProcessLauncher(),
            new NoopExitService(),
            AllowInstallSafetyGuard.Instance,
            progressWindow);
        var update = new AppUpdateCheckResponse { InstallerType = "exe" };

        await launcher.LaunchAsync(@"C:\Temp\hbpos.exe", update);
        Assert.Null(progressWindow.LastMode);

        await launcher.LaunchUnattendedAsync(@"C:\Temp\hbpos.exe", update, UnattendedInstallMode.AgentOnly);
        Assert.Same(UnattendedInstallMode.AgentOnly, progressWindow.LastMode);
    }

    [Fact]
    public async Task Agent_only_unattended_install_never_runs_the_installer_directly_when_update_window_is_unavailable()
    {
        var processLauncher = new CapturingProcessLauncher();
        var exitService = new CountingExitService();
        var launcher = new AppUpdateInstallerLauncher(
            processLauncher,
            exitService,
            AllowInstallSafetyGuard.Instance,
            new NullProgressWindowLauncher());

        var result = await launcher.LaunchUnattendedAsync(
            @"C:\Temp\hbpos.exe",
            new AppUpdateCheckResponse { InstallerType = "exe", InstallerArguments = "/VERYSILENT" },
            UnattendedInstallMode.AgentOnly);

        // 中文注释：直接运行安装器会弹 UAC，夜间无人点击；宁可失败让收银机继续用旧版，也不能卡到早上。
        Assert.False(result.Success);
        Assert.Null(processLauncher.FileName);
        Assert.Equal(0, exitService.ExitCalls);
    }

    [Fact]
    public async Task Agent_only_unattended_install_refuses_msi_packages()
    {
        var processLauncher = new CapturingProcessLauncher();
        var launcher = new AppUpdateInstallerLauncher(
            processLauncher,
            new CountingExitService(),
            AllowInstallSafetyGuard.Instance,
            new RecordingProgressWindowLauncher());

        var result = await launcher.LaunchUnattendedAsync(
            @"C:\Temp\hbpos.msi",
            new AppUpdateCheckResponse { InstallerType = "msi" },
            UnattendedInstallMode.AgentOnly);

        Assert.False(result.Success);
        Assert.Null(processLauncher.FileName);
    }

    [Fact]
    public async Task Any_installer_unattended_mode_and_attended_mode_still_fall_back_to_direct_install()
    {
        var processLauncher = new CapturingProcessLauncher();
        var launcher = new AppUpdateInstallerLauncher(
            processLauncher,
            new CountingExitService(),
            AllowInstallSafetyGuard.Instance,
            new NullProgressWindowLauncher());
        var update = new AppUpdateCheckResponse { InstallerType = "exe", InstallerArguments = "/VERYSILENT" };

        Assert.True((await launcher.LaunchAsync(@"C:\Temp\hbpos.exe", update)).Success);
        Assert.Equal(@"C:\Temp\hbpos.exe", processLauncher.FileName);

        processLauncher.Reset();
        Assert.True((await launcher.LaunchUnattendedAsync(@"C:\Temp\hbpos.exe", update, UnattendedInstallMode.AnyInstaller)).Success);
        Assert.Equal(@"C:\Temp\hbpos.exe", processLauncher.FileName);
    }

    [Fact]
    public async Task TryLaunchAsync_returns_null_when_install_has_no_updater()
    {
        Directory.Delete(UpdaterSourceDirectory, recursive: true);
        var processLauncher = new CapturingProcessLauncher();
        var launcher = CreateLauncher(processLauncher);

        var result = await launcher.TryLaunchAsync("setup.exe", string.Empty, new AppUpdateCheckResponse());

        Assert.Null(result);
        Assert.Null(processLauncher.FileName);
    }

    [Fact]
    public async Task TryLaunchAsync_returns_null_when_updater_process_cannot_start()
    {
        var processLauncher = new CapturingProcessLauncher { Result = ProcessLaunchResult.Fail("blocked") };
        var launcher = CreateLauncher(processLauncher);

        var result = await launcher.TryLaunchAsync("setup.exe", string.Empty, new AppUpdateCheckResponse());

        Assert.Null(result);
        Assert.NotNull(processLauncher.FileName);
    }

    [Fact]
    public void TryStageUpdater_removes_previous_staging_directories()
    {
        var stale = Path.Combine(StagingRoot, "20260101000000-old");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "Hbpos.Updater.exe"), "old");
        var launcher = CreateLauncher(new CapturingProcessLauncher());

        var staged = launcher.TryStageUpdater();

        Assert.NotNull(staged);
        Assert.False(Directory.Exists(stale));
        Assert.Single(Directory.GetDirectories(StagingRoot));
    }

    [Fact]
    public void TryPrepareLogPath_keeps_only_recent_install_logs()
    {
        Directory.CreateDirectory(LogDirectory);
        for (var index = 0; index < 12; index++)
        {
            var path = Path.Combine(LogDirectory, $"install-1.0.{index}-old.log");
            File.WriteAllText(path, "log");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index));
        }

        File.WriteAllText(Path.Combine(LogDirectory, "keep-me.txt"), "other");
        var launcher = CreateLauncher(new CapturingProcessLauncher());

        var logPath = launcher.TryPrepareLogPath("1.9.0 beta/1");

        Assert.Equal(Path.Combine(LogDirectory, "install-1.9.0beta1-20260926-101500.log"), logPath);
        var remaining = Directory.GetFiles(LogDirectory, "install-*.log").Select(Path.GetFileName).ToArray();
        Assert.Equal(AppUpdateProgressWindowLauncher.RetainedInstallLogCount - 1, remaining.Length);
        Assert.DoesNotContain("install-1.0.0-old.log", remaining);
        Assert.Contains("install-1.0.11-old.log", remaining);
        Assert.True(File.Exists(Path.Combine(LogDirectory, "keep-me.txt")));
    }

    [Fact]
    public async Task Registered_installer_launcher_routes_exe_updates_through_progress_window()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, null, null));
        var progressWindow = new RecordingProgressWindowLauncher();
        var processLauncher = new CapturingProcessLauncher();
        services.AddSingleton<IAppUpdateProgressWindowLauncher>(progressWindow);
        services.AddSingleton<IProcessLauncher>(processLauncher);
        services.AddSingleton<IAppUpdateInstallSafetyGuard>(AllowInstallSafetyGuard.Instance);
        services.AddSingleton<IApplicationExitService>(new NoopExitService());
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IAppUpdateInstallerLauncher>().LaunchAsync(
            @"C:\Temp\hbpos.exe",
            new AppUpdateCheckResponse { InstallerType = "exe" });

        Assert.True(result.Success);
        Assert.Equal(@"C:\Temp\hbpos.exe", progressWindow.InstallerPath);
        Assert.Null(processLauncher.FileName);
    }

    private AppUpdateProgressWindowLauncher CreateLauncher(CapturingProcessLauncher processLauncher)
    {
        return new AppUpdateProgressWindowLauncher(
            processLauncher,
            new FixedVersionProvider("1.8.3"),
            new AppUpdateProgressWindowOptions(
                UpdaterSourceDirectory,
                StagingRoot,
                LogDirectory,
                @"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe",
                4242),
            _time,
            () => "zh-CN");
    }

    private sealed class RecordingProgressWindowLauncher : IAppUpdateProgressWindowLauncher
    {
        public string? InstallerPath { get; private set; }

        public UnattendedInstallMode? LastMode { get; private set; }

        public Task<ProcessLaunchResult?> TryLaunchAsync(
            string installerPath,
            string installerArguments,
            AppUpdateCheckResponse update)
        {
            InstallerPath = installerPath;
            LastMode = null;
            return Task.FromResult<ProcessLaunchResult?>(ProcessLaunchResult.Succeeded());
        }

        public Task<ProcessLaunchResult?> TryLaunchAsync(
            string installerPath,
            string installerArguments,
            AppUpdateCheckResponse update,
            UnattendedInstallMode? unattended)
        {
            InstallerPath = installerPath;
            LastMode = unattended;
            return Task.FromResult<ProcessLaunchResult?>(ProcessLaunchResult.Succeeded());
        }
    }

    // 中文注释：本机没有可用的更新窗口（例如安装目录里缺 updater 文件夹），启动器只能退回直接运行安装器。
    private sealed class NullProgressWindowLauncher : IAppUpdateProgressWindowLauncher
    {
        public Task<ProcessLaunchResult?> TryLaunchAsync(
            string installerPath,
            string installerArguments,
            AppUpdateCheckResponse update) =>
            Task.FromResult<ProcessLaunchResult?>(null);
    }

    private sealed class CountingExitService : IApplicationExitService
    {
        public int ExitCalls { get; private set; }

        public void Exit() => ExitCalls++;
    }

    private sealed class NoopExitService : IApplicationExitService
    {
        public void Exit()
        {
        }
    }

    private sealed class FixedVersionProvider(string version) : IAppVersionProvider
    {
        public string CurrentVersion => version;
    }

    private sealed class CapturingProcessLauncher : IProcessLauncher
    {
        public string? FileName { get; private set; }

        public string? Arguments { get; private set; }

        public ProcessLaunchResult Result { get; init; } = ProcessLaunchResult.Succeeded();

        public void Reset()
        {
            FileName = null;
            Arguments = null;
        }

        public Task<ProcessLaunchResult> StartAsync(string fileName, string arguments)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(Result);
        }
    }
}
