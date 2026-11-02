using Hbpos.Updater;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class UpdateAgentTests
{
    private const string AppDirectory = @"C:\Program Files\HB POS";
    private const string RequestId = "0123456789abcdef0123456789abcdef";
    private const string TrustedUrl = "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/production/1.9.0/Hbpos.Client.Wpf-1.9.0-x64.exe";
    private static readonly string Sha256 = new('a', 64);

    private static UpdateAgentRequest CreateRequest()
    {
        Assert.True(UpdateAgentRequest.TryCreate(RequestId, TrustedUrl, Sha256, 4096, out var request, out _));
        return request!;
    }

    [Fact]
    public void Dispatch_records_acceptance_and_moves_worker_out_of_install_folder()
    {
        var harness = new AgentHarness();

        var exitCode = harness.Agent.Dispatch(CreateRequest().Encode(), harness.Layout.UpdaterDirectory);

        Assert.Equal(0, exitCode);
        Assert.Equal(UpdateAgentState.Accepted, harness.LastStatus.State);
        var (source, destination) = Assert.Single(harness.Host.CopiedDirectories);
        Assert.Equal(harness.Layout.UpdaterDirectory, source);
        Assert.Equal(harness.Layout.RuntimeDirectory(RequestId), destination);
        var (executable, arguments) = Assert.Single(harness.Host.StartedProcesses);
        Assert.Equal(Path.Combine(destination, UpdateAgentLayout.UpdaterExecutableName), executable);
        Assert.Equal($"--agent-worker {CreateRequest().Encode()}", arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Dispatch_rejects_invalid_request_without_touching_disk(string? encoded)
    {
        var harness = new AgentHarness();

        Assert.Equal(UpdateAgent.InvalidRequestExitCode, harness.Agent.Dispatch(encoded, harness.Layout.UpdaterDirectory));

        Assert.Empty(harness.Host.Statuses);
        Assert.Empty(harness.Host.CopiedDirectories);
        Assert.Empty(harness.Host.StartedProcesses);
    }

    [Fact]
    public void Dispatch_reports_failure_when_worker_cannot_start()
    {
        var harness = new AgentHarness { Host = { StartProcessException = new IOException("denied") } };

        Assert.Equal(UpdateAgent.AgentFailureExitCode, harness.Agent.Dispatch(CreateRequest().Encode(), harness.Layout.UpdaterDirectory));

        Assert.Equal(UpdateAgentState.Failed, harness.LastStatus.State);
        Assert.StartsWith("dispatch-failed", harness.LastStatus.Error);
    }

    [Fact]
    public async Task Worker_downloads_verifies_and_installs_with_fixed_silent_arguments()
    {
        var harness = new AgentHarness();

        var exitCode = await harness.Agent.RunWorkerAsync(CreateRequest().Encode(), harness.RuntimeDirectory, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [UpdateAgentState.Downloading, UpdateAgentState.Installing, UpdateAgentState.Succeeded],
            harness.Host.Statuses.Select(status => status.State).ToArray());
        var download = Assert.Single(harness.Host.Downloads);
        Assert.Equal(TrustedUrl, download.Url.AbsoluteUri);
        Assert.Equal(harness.Layout.PackageFile(Sha256) + ".download", download.Destination);
        Assert.Equal(4096, download.ExpectedSize);

        var install = Assert.Single(harness.Host.Installs);
        Assert.Equal(harness.Layout.PackageFile(Sha256), install.Path);
        Assert.StartsWith(UpdateAgent.FixedInstallerArguments, install.Arguments);
        Assert.Contains("/HBPOSAGENT=1", install.Arguments);
        Assert.Contains($"/HBPOSPROGRESS=\"{harness.Layout.ProgressFile(RequestId)}\"", install.Arguments);
        Assert.Contains($"/LOG=\"{harness.Layout.LogFile(RequestId)}\"", install.Arguments);
        Assert.Equal(UpdateAgent.InstallTimeout, install.Timeout);
        Assert.Equal(harness.Layout.WorkerLockFile, harness.Host.LockPaths.Single());
        Assert.Equal(1, harness.Host.CleanupCalls);
        Assert.False(harness.Host.LockHeld);
    }

    [Fact]
    public async Task Worker_reuses_cached_package_when_hash_still_matches()
    {
        var harness = new AgentHarness();
        harness.Host.Files[harness.Layout.PackageFile(Sha256)] = Sha256;

        Assert.Equal(0, await harness.Agent.RunWorkerAsync(CreateRequest().Encode(), harness.RuntimeDirectory, CancellationToken.None));

        Assert.Empty(harness.Host.Downloads);
        Assert.Single(harness.Host.Installs);
    }

    [Fact]
    public async Task Worker_redownloads_when_cached_package_hash_differs()
    {
        var harness = new AgentHarness();
        harness.Host.Files[harness.Layout.PackageFile(Sha256)] = new string('b', 64);

        Assert.Equal(0, await harness.Agent.RunWorkerAsync(CreateRequest().Encode(), harness.RuntimeDirectory, CancellationToken.None));

        Assert.Single(harness.Host.Downloads);
        Assert.Equal(Sha256, harness.Host.Files[harness.Layout.PackageFile(Sha256)]);
    }

    [Fact]
    public async Task Worker_refuses_to_install_package_whose_sha256_does_not_match()
    {
        var harness = new AgentHarness { Host = { DownloadedContentHash = new string('c', 64) } };

        var exitCode = await harness.Agent.RunWorkerAsync(CreateRequest().Encode(), harness.RuntimeDirectory, CancellationToken.None);

        // 中文注释：下载内容与请求里的 SHA256 不一致时不安装，也不留下这个文件。
        Assert.Equal(UpdateAgent.AgentFailureExitCode, exitCode);
        Assert.Equal(UpdateAgentState.Failed, harness.LastStatus.State);
        Assert.Equal("sha256-mismatch", harness.LastStatus.Error);
        Assert.Empty(harness.Host.Installs);
        Assert.DoesNotContain(harness.Host.Files.Keys, path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(harness.Host.Files.Keys, path => path.EndsWith(".download", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(IOException))]
    public async Task Worker_reports_download_failures_and_never_installs(Type exceptionType)
    {
        var harness = new AgentHarness { Host = { DownloadException = (Exception)Activator.CreateInstance(exceptionType, "boom")! } };

        Assert.Equal(UpdateAgent.AgentFailureExitCode, await harness.Agent.RunWorkerAsync(CreateRequest().Encode(), harness.RuntimeDirectory, CancellationToken.None));

        Assert.StartsWith("download-failed", harness.LastStatus.Error);
        Assert.Empty(harness.Host.Installs);
    }

    [Fact]
    public async Task Worker_reports_installer_exit_code_and_failure()
    {
        var harness = new AgentHarness { Host = { InstallerExitCode = 5 } };

        Assert.Equal(5, await harness.Agent.RunWorkerAsync(CreateRequest().Encode(), harness.RuntimeDirectory, CancellationToken.None));

        Assert.Equal(UpdateAgentState.Failed, harness.LastStatus.State);
        Assert.Equal(5, harness.LastStatus.ExitCode);
        Assert.Equal("installer-exit-code", harness.LastStatus.Error);
    }

    [Fact]
    public async Task Worker_reports_installer_timeout()
    {
        var harness = new AgentHarness { Host = { InstallException = new TimeoutException() } };

        Assert.Equal(UpdateAgent.AgentFailureExitCode, await harness.Agent.RunWorkerAsync(CreateRequest().Encode(), harness.RuntimeDirectory, CancellationToken.None));

        Assert.Equal("install-timeout", harness.LastStatus.Error);
    }

    [Fact]
    public async Task Worker_fails_fast_when_another_install_holds_the_lock()
    {
        var harness = new AgentHarness { Host = { LockAvailable = false } };

        Assert.Equal(UpdateAgent.AgentFailureExitCode, await harness.Agent.RunWorkerAsync(CreateRequest().Encode(), harness.RuntimeDirectory, CancellationToken.None));

        Assert.Equal("busy", harness.LastStatus.Error);
        Assert.Empty(harness.Host.Downloads);
        Assert.Empty(harness.Host.Installs);
    }

    [Fact]
    public async Task Worker_rejects_invalid_request()
    {
        var harness = new AgentHarness();

        Assert.Equal(UpdateAgent.InvalidRequestExitCode, await harness.Agent.RunWorkerAsync("garbage", harness.RuntimeDirectory, CancellationToken.None));

        Assert.Empty(harness.Host.Statuses);
        Assert.Empty(harness.Host.Downloads);
        Assert.Empty(harness.Host.Installs);
    }

    [Fact]
    public void Fixed_installer_arguments_are_silent_and_cannot_be_overridden_by_requests()
    {
        // 中文注释：请求里根本没有“安装参数”字段，参数全部由代理固定，避免调用方借代理执行任意命令行。
        Assert.Contains("/VERYSILENT", UpdateAgent.FixedInstallerArguments);
        Assert.Contains("/SUPPRESSMSGBOXES", UpdateAgent.FixedInstallerArguments);
        Assert.Contains("/NORESTART", UpdateAgent.FixedInstallerArguments);
        Assert.Contains("/HBPOSAGENT=1", UpdateAgent.FixedInstallerArguments);
        Assert.DoesNotContain(
            typeof(UpdateAgentRequest).GetProperties(),
            property => property.Name.Contains("Argument", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class AgentHarness
    {
        public AgentHarness()
        {
            Layout = new UpdateAgentLayout(AppDirectory);
            Agent = new UpdateAgent(Host, Layout, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        }

        public FakeAgentHost Host { get; } = new();

        public UpdateAgentLayout Layout { get; }

        public UpdateAgent Agent { get; }

        public string RuntimeDirectory => Layout.RuntimeDirectory(RequestId);

        public UpdateAgentStatus LastStatus => Host.Statuses[^1];
    }

    private sealed class FakeAgentHost : IUpdateAgentHost
    {
        public List<UpdateAgentStatus> Statuses { get; } = [];

        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Source, string Destination)> CopiedDirectories { get; } = [];

        public List<(string Executable, string Arguments)> StartedProcesses { get; } = [];

        public List<(Uri Url, string Destination, long ExpectedSize)> Downloads { get; } = [];

        public List<(string Path, string Arguments, TimeSpan Timeout)> Installs { get; } = [];

        public List<string> LockPaths { get; } = [];

        public int CleanupCalls { get; private set; }

        public bool LockAvailable { get; set; } = true;

        public bool LockHeld { get; private set; }

        public int InstallerExitCode { get; set; }

        public string? DownloadedContentHash { get; set; }

        public Exception? DownloadException { get; set; }

        public Exception? InstallException { get; set; }

        public Exception? StartProcessException { get; set; }

        public void EnsureDirectory(string path)
        {
        }

        public bool FileExists(string path) => Files.ContainsKey(path);

        public Task<string?> TryComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            return Task.FromResult(Files.TryGetValue(path, out var hash) ? hash : null);
        }

        public Task DownloadAsync(Uri url, string destinationPath, long expectedSize, CancellationToken cancellationToken)
        {
            Downloads.Add((url, destinationPath, expectedSize));
            if (DownloadException is { } exception)
            {
                return Task.FromException(exception);
            }

            Files[destinationPath] = DownloadedContentHash ?? Sha256;
            return Task.CompletedTask;
        }

        public void MoveFile(string sourcePath, string destinationPath)
        {
            Files[destinationPath] = Files[sourcePath];
            Files.Remove(sourcePath);
        }

        public void TryDeleteFile(string path) => Files.Remove(path);

        public void WriteAllTextAtomic(string path, string content)
        {
            Assert.True(UpdateAgentStatus.TryParse(content, RequestId, out var status));
            Statuses.Add(status!);
        }

        public void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            CopiedDirectories.Add((sourceDirectory, destinationDirectory));
        }

        public void StartDetachedProcess(string executablePath, string arguments)
        {
            if (StartProcessException is { } exception)
            {
                throw exception;
            }

            StartedProcesses.Add((executablePath, arguments));
        }

        public Task<int> RunInstallerAsync(string installerPath, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Installs.Add((installerPath, arguments, timeout));
            return InstallException is { } exception
                ? Task.FromException<int>(exception)
                : Task.FromResult(InstallerExitCode);
        }

        public IDisposable? TryAcquireWorkerLock(string lockFilePath, TimeSpan timeout)
        {
            LockPaths.Add(lockFilePath);
            if (!LockAvailable)
            {
                return null;
            }

            LockHeld = true;
            return new Lease(this);
        }

        public void CleanupStaleFiles(UpdateAgentLayout layout, string keepPackagePath, string currentRuntimeDirectory)
        {
            CleanupCalls++;
        }

        private sealed class Lease(FakeAgentHost host) : IDisposable
        {
            public void Dispose() => host.LockHeld = false;
        }
    }
}
