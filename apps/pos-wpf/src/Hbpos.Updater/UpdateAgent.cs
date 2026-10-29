using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Hbpos.Updater;

/// <summary>代理用到的文件、网络和进程操作；单独抽出来便于用假实现测试完整流程。</summary>
internal interface IUpdateAgentHost
{
    void EnsureDirectory(string path);

    bool FileExists(string path);

    Task<string?> TryComputeSha256Async(string path, CancellationToken cancellationToken);

    Task DownloadAsync(Uri url, string destinationPath, long expectedSize, CancellationToken cancellationToken);

    void MoveFile(string sourcePath, string destinationPath);

    void TryDeleteFile(string path);

    void WriteAllTextAtomic(string path, string content);

    void CopyDirectory(string sourceDirectory, string destinationDirectory);

    void StartDetachedProcess(string executablePath, string arguments);

    Task<int> RunInstallerAsync(string installerPath, string arguments, TimeSpan timeout, CancellationToken cancellationToken);

    IDisposable? TryAcquireWorkerLock(string lockFilePath, TimeSpan timeout);

    void CleanupStaleFiles(UpdateAgentLayout layout, string keepPackagePath, string currentRuntimeDirectory);
}

/// <summary>
/// 管理员更新代理（以 SYSTEM 运行）。任务计划先以 --agent 拉起 {app}\updater 里的程序：只校验请求、写“已受理”，
/// 再把自己复制到 {app}\UpdateAgent\runtime\&lt;id&gt; 以 --agent-worker 重启后立即退出，避免安装器替换 {app}\updater 时被占用。
/// worker 从公司 COS 发布目录自己重新下载安装包，核对大小和 SHA256，再用固定的静默参数安装，把结果写进状态文件。
/// </summary>
internal sealed class UpdateAgent(IUpdateAgentHost host, UpdateAgentLayout layout, TimeProvider timeProvider)
{
    public const string AgentOption = "--agent";
    public const string WorkerOption = "--agent-worker";
    public const string RegisterOption = "--register-agent-task";
    public const string UnregisterOption = "--unregister-agent-task";

    internal const int InvalidRequestExitCode = 3;
    internal const int AgentFailureExitCode = 4;

    // 中文注释：安装参数固定，不接受请求里的参数：/HBPOSAGENT=1 让安装脚本跳过“以原用户打开收银端”（SYSTEM 下会打开到服务会话），
    // 由登录会话里的更新窗口负责打开新版。
    internal const string FixedInstallerArguments =
        "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /HBPOSAGENT=1";

    internal static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan WorkerLockTimeout = TimeSpan.FromSeconds(5);

    public int Dispatch(string? encodedRequest, string updaterDirectory)
    {
        if (!UpdateAgentRequest.TryDecode(encodedRequest, out var request, out _) || request is null)
        {
            return InvalidRequestExitCode;
        }

        try
        {
            EnsureLayout();
            WriteStatus(request, UpdateAgentState.Accepted);
            var runtimeDirectory = layout.RuntimeDirectory(request.RequestId);
            host.CopyDirectory(updaterDirectory, runtimeDirectory);
            host.StartDetachedProcess(
                Path.Combine(runtimeDirectory, UpdateAgentLayout.UpdaterExecutableName),
                $"{WorkerOption} {request.Encode()}");
            return 0;
        }
        catch (Exception ex) when (IsAgentFailure(ex))
        {
            TryWriteStatus(request, UpdateAgentState.Failed, null, $"dispatch-failed {ex.GetType().Name}");
            return AgentFailureExitCode;
        }
    }

    public async Task<int> RunWorkerAsync(string? encodedRequest, string currentRuntimeDirectory, CancellationToken cancellationToken)
    {
        if (!UpdateAgentRequest.TryDecode(encodedRequest, out var request, out _) || request is null)
        {
            return InvalidRequestExitCode;
        }

        try
        {
            EnsureLayout();
            using var workerLock = host.TryAcquireWorkerLock(layout.WorkerLockFile, WorkerLockTimeout);
            if (workerLock is null)
            {
                // 中文注释：同一时间只允许一个安装在跑，另一个请求直接失败，由更新窗口按失败处理。
                WriteStatus(request, UpdateAgentState.Failed, null, "busy");
                return AgentFailureExitCode;
            }

            WriteStatus(request, UpdateAgentState.Downloading);
            var packagePath = layout.PackageFile(request.Sha256);
            var packageError = await EnsureVerifiedPackageAsync(request, packagePath, cancellationToken);
            if (packageError is not null)
            {
                WriteStatus(request, UpdateAgentState.Failed, null, packageError);
                return AgentFailureExitCode;
            }

            WriteStatus(request, UpdateAgentState.Installing);
            var arguments =
                $"{FixedInstallerArguments} /HBPOSPROGRESS=\"{layout.ProgressFile(request.RequestId)}\" /LOG=\"{layout.LogFile(request.RequestId)}\"";
            int exitCode;
            try
            {
                exitCode = await host.RunInstallerAsync(packagePath, arguments, InstallTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                WriteStatus(request, UpdateAgentState.Failed, null, "install-timeout");
                return AgentFailureExitCode;
            }

            WriteStatus(
                request,
                exitCode == 0 ? UpdateAgentState.Succeeded : UpdateAgentState.Failed,
                exitCode,
                exitCode == 0 ? null : "installer-exit-code");
            host.CleanupStaleFiles(layout, packagePath, currentRuntimeDirectory);
            return exitCode;
        }
        catch (Exception ex) when (IsAgentFailure(ex))
        {
            TryWriteStatus(request, UpdateAgentState.Failed, null, $"agent-failed {ex.GetType().Name}");
            return AgentFailureExitCode;
        }
    }

    private async Task<string?> EnsureVerifiedPackageAsync(
        UpdateAgentRequest request,
        string packagePath,
        CancellationToken cancellationToken)
    {
        // 中文注释：同一安装包之前下过且哈希仍一致时直接复用；任何不一致都重新下载，不信任已有文件。
        if (host.FileExists(packagePath) &&
            string.Equals(await host.TryComputeSha256Async(packagePath, cancellationToken), request.Sha256, StringComparison.Ordinal))
        {
            return null;
        }

        var downloadPath = packagePath + ".download";
        host.TryDeleteFile(downloadPath);
        try
        {
            await host.DownloadAsync(request.DownloadUrl, downloadPath, request.FileSize, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            host.TryDeleteFile(downloadPath);
            return $"download-failed {ex.GetType().Name}";
        }

        var actualSha256 = await host.TryComputeSha256Async(downloadPath, cancellationToken);
        if (!string.Equals(actualSha256, request.Sha256, StringComparison.Ordinal))
        {
            host.TryDeleteFile(downloadPath);
            return "sha256-mismatch";
        }

        host.MoveFile(downloadPath, packagePath);
        return null;
    }

    private void EnsureLayout()
    {
        host.EnsureDirectory(layout.PackagesDirectory);
        host.EnsureDirectory(layout.StatusDirectory);
        host.EnsureDirectory(layout.LogsDirectory);
        host.EnsureDirectory(layout.RuntimeRootDirectory);
    }

    private void WriteStatus(UpdateAgentRequest request, UpdateAgentState state, int? exitCode = null, string? error = null)
    {
        var status = new UpdateAgentStatus(request.RequestId, state, exitCode, error, timeProvider.GetUtcNow());
        host.WriteAllTextAtomic(layout.StatusFile(request.RequestId), status.Serialize());
    }

    private void TryWriteStatus(UpdateAgentRequest request, UpdateAgentState state, int? exitCode, string? error)
    {
        try
        {
            WriteStatus(request, state, exitCode, error);
        }
        catch (Exception ex) when (IsAgentFailure(ex))
        {
            // 中文注释：连状态都写不了时只能退出，更新窗口会因收不到结果按超时处理。
        }
    }

    private static bool IsAgentFailure(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or Win32Exception
            or InvalidOperationException
            or HttpRequestException;
    }
}

internal sealed class WindowsUpdateAgentHost : IUpdateAgentHost
{
    private const int RetainedPackageCount = 2;
    private static readonly TimeSpan StaleFileAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    private const int CopyBufferSize = 81920;

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public async Task<string?> TryComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task DownloadAsync(Uri url, string destinationPath, long expectedSize, CancellationToken cancellationToken)
    {
        // 中文注释：不跟随重定向，只从已校验的 COS 源站下载；逐块限制大小，超出合同立即中止。
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = DownloadTimeout };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != expectedSize)
        {
            throw new InvalidDataException("Update package size does not match the request.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            copied += read;
            if (copied > expectedSize)
            {
                throw new InvalidDataException("Update package is larger than the request.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (copied != expectedSize)
        {
            throw new InvalidDataException("Update package size does not match the request.");
        }
    }

    public void MoveFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: true);

    public void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void WriteAllTextAtomic(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }

    public void StartDetachedProcess(string executablePath, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
        }) ?? throw new InvalidOperationException("Process.Start returned null.");
    }

    public async Task<int> RunInstallerAsync(string installerPath, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty
        }) ?? throw new InvalidOperationException("Process.Start returned null.");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Installer did not finish in time.");
        }

        return process.ExitCode;
    }

    public IDisposable? TryAcquireWorkerLock(string lockFilePath, TimeSpan timeout)
    {
        // 中文注释：用独占打开的锁文件而不是 Mutex：worker 全程 async，Mutex 必须在获取它的线程上释放，文件锁没有线程归属问题；
        // 进程退出时系统自动关闭句柄，异常退出也不会留下死锁。
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public void CleanupStaleFiles(UpdateAgentLayout layout, string keepPackagePath, string currentRuntimeDirectory)
    {
        TryCleanup(() =>
        {
            var stalePackages = new DirectoryInfo(layout.PackagesDirectory)
                .GetFiles("*.exe")
                .Where(file => !string.Equals(file.FullName, Path.GetFullPath(keepPackagePath), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(RetainedPackageCount - 1);
            foreach (var package in stalePackages)
            {
                package.Delete();
            }
        });

        // 中文注释：worker 传入的当前运行目录来自 AppContext.BaseDirectory，带结尾反斜杠；而列举出的目录不带，必须先统一再比较，
        // 否则会把自己正在运行的目录当成旧目录去删。
        var currentRuntime = NormalizeDirectory(currentRuntimeDirectory);
        TryCleanup(() =>
        {
            foreach (var runtime in Directory.GetDirectories(layout.RuntimeRootDirectory))
            {
                if (!string.Equals(NormalizeDirectory(runtime), currentRuntime, StringComparison.OrdinalIgnoreCase))
                {
                    TryCleanup(() => Directory.Delete(runtime, recursive: true));
                }
            }
        });

        var threshold = DateTime.UtcNow - StaleFileAge;
        foreach (var directory in new[] { layout.StatusDirectory, layout.LogsDirectory })
        {
            TryCleanup(() =>
            {
                foreach (var file in new DirectoryInfo(directory).GetFiles().Where(file => file.LastWriteTimeUtc < threshold))
                {
                    TryCleanup(file.Delete);
                }
            });
        }
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 中文注释：清理失败不影响本次安装结果，下次再清。
        }
    }
}
