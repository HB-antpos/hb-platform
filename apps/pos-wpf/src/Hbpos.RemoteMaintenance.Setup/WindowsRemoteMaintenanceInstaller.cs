using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Hbpos.RemoteMaintenance.Setup;

public interface IRemoteMaintenanceCommandRunner
{
    Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);
    async Task<RemoteMaintenanceCommandResult> RunWithOutputAsync(string fileName, string arguments, CancellationToken cancellationToken) =>
        new(await RunAsync(fileName, arguments, cancellationToken), string.Empty, string.Empty);
    // 是否仍有与正式安装程序同名、但不是从正式安装位置启动的进程在运行（即脱离外壳的内层安装进程）。
    // 默认实现供测试替身使用，视为没有后台安装进程。
    bool IsDetachedProcessRunning(string installedExecutable) => false;
}
public sealed record RemoteMaintenanceCommandResult(int ExitCode, string StandardOutput, string StandardError);
public interface IRemoteMaintenanceServiceControl
{
    Task<string?> QueryAsync(string serviceName, CancellationToken cancellationToken);
    Task<int> CreateOrUpdateAsync(string serviceName, string binaryPath, string accountName, CancellationToken cancellationToken);
    Task<int> StartAsync(string serviceName, CancellationToken cancellationToken);
    Task<int> StopAsync(string serviceName, CancellationToken cancellationToken);
}

public sealed class WindowsRemoteMaintenanceCommandRunner : IRemoteMaintenanceCommandRunner
{
    public async Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        // 静默安装会派生常驻托盘进程。只需退出码的命令不重定向输出，
        // 避免后台进程继承管道后一直没有 EOF，导致安装已完成却等待超时。
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(fileName))!
        }};
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public async Task<RemoteMaintenanceCommandResult> RunWithOutputAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(fileName))!
        }};
        process.Start();
        // 同时读取两个管道，禁止记录参数；其中可能含本机生成的无人值守密码。
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            return new(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public bool IsDetachedProcessRunning(string installedExecutable)
    {
        var installed = Path.GetFullPath(installedExecutable);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(installed)))
        {
            using (process)
            {
                var path = TryGetImagePath(process.Id);
                if (path is not null && !string.Equals(path, installed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // 读内核记录的映像路径：外壳退出时内层进程可能刚创建、模块表尚未初始化，
    // Process.MainModule 此时会读取失败而漏判。PROCESS_QUERY_LIMITED_INFORMATION
    // 对 SYSTEM 服务进程同样可用；仍打不开的不是本次提权用户启动的安装进程。
    private static string? TryGetImagePath(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? Path.GetFullPath(buffer.ToString(0, size)) : null;
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder exeName, ref int size);

    // Windows CRT 参数编码：内层引号与末尾反斜杠需要分别处理，尤其 SCM binPath。
    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') result.Append('\\', backslashes * 2 + 1).Append(c);
            else result.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
}

public sealed class WindowsRemoteMaintenanceServiceControl(IRemoteMaintenanceCommandRunner commandRunner) : IRemoteMaintenanceServiceControl
{
    private static string ScPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
    private static string Q(string value) => WindowsRemoteMaintenanceCommandRunner.Quote(value);
    public Task<string?> QueryAsync(string serviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var service = new ServiceController(serviceName);
            return Task.FromResult<string?>(service.Status switch
            {
                ServiceControllerStatus.Running => "running",
                ServiceControllerStatus.StartPending => "starting",
                ServiceControllerStatus.StopPending => "stopping",
                _ => "stopped"
            });
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 })
        { return Task.FromResult<string?>(null); }
    }
    public async Task<int> CreateOrUpdateAsync(string serviceName, string binaryPath, string accountName, CancellationToken cancellationToken)
    {
        var action = await QueryAsync(serviceName, cancellationToken) is null ? "create" : "config";
        return await commandRunner.RunAsync(ScPath,
            $"{action} {Q(serviceName)} binPath= {Q(binaryPath)} start= auto obj= {Q(accountName)}", cancellationToken);
    }
    public async Task<int> StartAsync(string serviceName, CancellationToken cancellationToken)
    {
        if (await QueryAsync(serviceName, cancellationToken) == "running") return 0;
        var result = await commandRunner.RunAsync(ScPath, $"start {Q(serviceName)}", cancellationToken);
        if (result is not (0 or 1056)) return result;
        return await WaitAsync(serviceName, "running", cancellationToken) ? 0 : 1460;
    }
    public async Task<int> StopAsync(string serviceName, CancellationToken cancellationToken)
    {
        var current = await QueryAsync(serviceName, cancellationToken);
        if (current is null or "stopped") return 0;
        var result = await commandRunner.RunAsync(ScPath, $"stop {Q(serviceName)}", cancellationToken);
        if (result is not (0 or 1062)) return result;
        return await WaitAsync(serviceName, "stopped", cancellationToken) ? 0 : 1460;
    }
    private async Task<bool> WaitAsync(string name, string expected, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 60; i++)
        {
            if (await QueryAsync(name, cancellationToken) == expected) return true;
            await Task.Delay(500, cancellationToken);
        }
        return false;
    }
}

/// <summary>只允许公司服务器和官方固定版本；提权时重新校验，绝不执行 journal 指定的任意文件。</summary>
public sealed class WindowsRemoteMaintenanceInstaller(
    IRemoteMaintenanceCommandRunner commandRunner,
    IRemoteMaintenanceServiceControl serviceControl,
    Func<RemoteMaintenanceInstallationResult, Task>? progressWriter = null) : IRemoteMaintenanceInstaller
{
    internal const string StatusServiceName = "HBPOSRemoteStatus";
    private const string RustDeskServiceName = "RustDesk";
    internal const string TrustedRustDeskSha256 = "eaedeb0088e687bf46f7c46a9c6ea5493ce51f3134dfd6acbedb47b5b9136274";
    private const long TrustedRustDeskSize = 24472432;
    private static string ProgramRoot => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string StatusProgramDirectory => Path.Combine(ProgramRoot, "HBPOS", "RemoteStatus");
    private static string InstalledRustDesk => Path.Combine(ProgramRoot, "RustDesk", "rustdesk.exe");
    private static string Q(string value) => WindowsRemoteMaintenanceCommandRunner.Quote(value);
    // 生产值覆盖慢盘 XCOPY、杀毒扫描和防火墙规则写入；测试可缩短，避免真实等待。
    internal TimeSpan SetupWaitTimeout { get; init; } = TimeSpan.FromMinutes(5);
    internal TimeSpan DaemonReadyTimeout { get; init; } = TimeSpan.FromSeconds(90);
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<RemoteMaintenanceInstallationResult> InstallAsync(RemoteMaintenanceInstallationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateConfig(request.Prepare.Config);
        if (request.Prepare.OperationId == Guid.Empty || request.Prepare.DeviceId == Guid.Empty ||
            request.Password.Length is < 12 or > 128 || request.Password.Any(char.IsControl))
            throw new InvalidDataException("远程维护安装参数无效。");
        // 先确认随 WPF 发布的 Agent 位于 Program Files；禁止从下载 ZIP/用户 journal 提权复制程序。
        var statusSource = ResolveTrustedStatusAgentPath();
        await EnsureNoForeignRustDeskConfigAsync(request.Prepare.Config, cancellationToken);
        Directory.CreateDirectory(StatusProgramDirectory);
        EnsureProtectedProgramPath(StatusProgramDirectory);
        ApplyDirectoryAcl(StatusProgramDirectory, localServiceCanRead: true);
        var staged = Path.Combine(StatusProgramDirectory, "rustdesk-install-" + Guid.NewGuid().ToString("N") + ".exe");
        var rustDeskTouched = false;
        var statusAgentTouched = false;
        try
        {
            // 源文件打开期间禁止写入/删除，复制到管理员目录后再验证并执行，封住校验到执行的替换窗口。
            await using (var source = new FileStream(request.RustDeskArtifactPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var target = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (source.Length != TrustedRustDeskSize) throw new InvalidDataException("RustDesk 文件大小错误。");
                await source.CopyToAsync(target, cancellationToken);
            }
            await using (var check = File.OpenRead(staged))
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken)), TrustedRustDeskSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("RustDesk 官方文件哈希错误。");
            // 外部配置预检查在此之前完成；从实际触发 RustDesk 安装起才允许外层清理该服务。
            if (progressWriter is not null)
                await progressWriter(new(true, false, string.Empty, request.ClientVersion, request.DataDirectory));
            rustDeskTouched = true;
            if (await commandRunner.RunAsync(staged, "--silent-install", cancellationToken) != 0)
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskSetupIncomplete);
            // 官方安装包是自解压外壳：解包后拉起内层 rustdesk.exe 执行安装脚本就立即退出，
            // 退出码 0 只代表“解包完成”。安装脚本先卸载旧版、XCOPY 整个目录，最后才创建服务；
            // 不等它结束就会对着复制中的目录执行命令，且安装会在 helper 失败退出后继续跑完，
            // 留下一个未配置公司服务器、却开机自启的 RustDesk。
            await WaitForDetachedRustDeskSetupAsync(cancellationToken);
            if (!File.Exists(InstalledRustDesk))
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskSetupIncomplete);
            EnsureProtectedProgramPath(InstalledRustDesk);
            if (await serviceControl.QueryAsync(RustDeskServiceName, cancellationToken) is null &&
                await commandRunner.RunAsync(InstalledRustDesk, "--install-service", cancellationToken) != 0)
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskSetupIncomplete);
            if (await serviceControl.StartAsync(RustDeskServiceName, cancellationToken) != 0)
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskSetupIncomplete);

            await ConfigureRustDeskAsync(InstalledRustDesk, request.Prepare.Config, request.Password, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(StatusProgramDirectory, "managed-server.json"),
                JsonSerializer.Serialize(request.Prepare.Config), cancellationToken);
            var rustdeskId = await GetRustdeskIdAsync(cancellationToken);
            if (string.IsNullOrEmpty(rustdeskId))
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskConfigurationFailed);
            // 停止旧状态服务是本次安装对它的第一次实际修改，失败时也必须保留清理依据。
            if (progressWriter is not null)
                await progressWriter(new(true, true, rustdeskId, request.ClientVersion, request.DataDirectory));
            statusAgentTouched = true;
            if (await serviceControl.StopAsync(StatusServiceName, cancellationToken) != 0)
                throw new InvalidOperationException("旧状态服务无法停止。");
            var statusBinary = Path.Combine(StatusProgramDirectory, "Hbpos.RemoteStatus.exe");
            if (!string.Equals(Path.GetFullPath(statusSource), Path.GetFullPath(statusBinary), StringComparison.OrdinalIgnoreCase))
                File.Copy(statusSource, statusBinary, overwrite: true);
            if (await serviceControl.CreateOrUpdateAsync(StatusServiceName, Q(statusBinary) + " --service", "NT AUTHORITY\\LocalService", cancellationToken) != 0)
                throw new InvalidOperationException("状态服务安装失败。");
            return new(true, true, rustdeskId, request.ClientVersion, request.DataDirectory);
        }
        catch
        {
            if (statusAgentTouched)
                try { await serviceControl.StopAsync(StatusServiceName, CancellationToken.None); } catch { }
            if (rustDeskTouched)
                try { await serviceControl.StopAsync(RustDeskServiceName, CancellationToken.None); } catch { }
            throw;
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    internal async Task ConfigureRustDeskAsync(string executable, RemoteMaintenanceConfig config, string password, CancellationToken cancellationToken)
    {
        ValidateConfig(config);
        // --password 只有经 IPC 得到服务端 --server 进程确认才输出 Done!，而服务刚启动时 IPC
        // 尚未就绪（CLI 连接超时仅 1 秒）。先重试到确认成功：既设置了密码，也证明 IPC 已通，
        // 之后的 --config/--option 才会写入服务配置，而不是静默回退写到当前管理员的本地配置。
        await WaitForDaemonPasswordAckAsync(executable, password, cancellationToken);
        // 官方 CLI 按精确参数数量分支，--config 和 --password 必须分开调用。
        var value = $"host={config.IdServer},key={config.PublicKey},relay={config.RelayServer},";
        if (await commandRunner.RunAsync(executable, "--config " + Q(value), cancellationToken) != 0)
            throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskConfigurationFailed);
        // 仅密码认证允许无人值守；禁止仅靠界面点击确认，逐项读回真实服务配置。
        foreach (var option in new[] { ("verification-method", "use-permanent-password"), ("approve-mode", "password"), ("allow-only-conn-window-open", "N") })
            if (await commandRunner.RunAsync(executable, "--option " + Q(option.Item1) + " " + Q(option.Item2), cancellationToken) != 0)
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskConfigurationFailed);
        var expected = new[] { ("custom-rendezvous-server", config.IdServer), ("relay-server", config.RelayServer), ("key", config.PublicKey),
            ("verification-method", "use-permanent-password"), ("approve-mode", "password"), ("allow-only-conn-window-open", "N") };
        foreach (var option in expected)
            if (!await OptionMatchesAsync(executable, option.Item1, option.Item2, cancellationToken))
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskConfigurationFailed);
    }

    internal async Task WaitForDetachedRustDeskSetupAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (commandRunner.IsDetachedProcessRunning(InstalledRustDesk))
        {
            // 超时不强杀安装进程：中途打断会留下半装状态，交给界面提示卸载后重试。
            if (elapsed.Elapsed >= SetupWaitTimeout)
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskSetupIncomplete);
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task WaitForDaemonPasswordAckAsync(string executable, string password, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var result = await commandRunner.RunWithOutputAsync(executable, "--password " + Q(password), cancellationToken);
            if (result.ExitCode == 0 && result.StandardOutput.Trim() == "Done!") return;
            if (elapsed.Elapsed >= DaemonReadyTimeout)
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskConfigurationFailed);
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    // 服务刚启动时 IPC 可能短暂不可用，CLI 会回退读取当前用户的本地配置；
    // 读回允许有限次重试，全部不一致才判定为不一致。
    private async Task<bool> OptionMatchesAsync(string executable, string key, string expected, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var actual = await commandRunner.RunWithOutputAsync(executable, "--option " + Q(key), cancellationToken);
            if (actual.ExitCode == 0 && actual.StandardOutput.Trim() == expected) return true;
            if (attempt >= 10) return false;
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public async Task ConfigureStatusAgentAsync(RemoteMaintenancePrepareResponse prepare, RemoteMaintenanceCommitResponse commit,
        string rustdeskId, string clientVersion, CancellationToken cancellationToken = default)
    {
        if (commit.DeviceId != prepare.DeviceId || !Uri.TryCreate(commit.HeartbeatUrl, UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.Host != "hotbargain.vip" || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) ||
            url.AbsolutePath != $"/api/remote-maintenance/devices/{commit.DeviceId:D}/heartbeat")
            throw new InvalidDataException("状态服务地址不属于公司设备接口。");
        if (await serviceControl.StopAsync(StatusServiceName, cancellationToken) != 0)
            throw new InvalidOperationException("状态服务无法停止以应用配置。");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HBPOS", "RemoteStatus");
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        ApplyDirectoryAcl(directory, localServiceCanRead: true);
        var sequenceDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(sequenceDirectory);
        ApplyDirectoryAcl(sequenceDirectory, localServiceCanRead: true, localServiceCanWrite: true);
        var bytes = Encoding.UTF8.GetBytes(commit.MonitorToken);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var temporary = Path.Combine(directory, "agent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
            {
                agentVersion = prepare.ArtifactManifest.StatusAgent.Version,
                rustDeskId = rustdeskId, clientVersion, heartbeatUrl = url.AbsoluteUri,
                protectedMonitorToken = Convert.ToBase64String(encrypted)
            }), cancellationToken);
            File.Move(temporary, Path.Combine(directory, "agent.json"), overwrite: true);
            if (await serviceControl.StartAsync(RustDeskServiceName, cancellationToken) != 0 ||
                await serviceControl.StartAsync(StatusServiceName, cancellationToken) != 0)
                throw new InvalidOperationException("状态服务未启动。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task FailClosedAsync(RemoteMaintenanceInstallationResult installation, CancellationToken cancellationToken = default)
    {
        if (installation.StatusAgentInstalled) await serviceControl.StopAsync(StatusServiceName, cancellationToken);
        if (installation.RustDeskInstalled) await serviceControl.StopAsync(RustDeskServiceName, cancellationToken);
    }
    public async Task<string?> GetRustdeskIdAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(InstalledRustDesk)) return null;
        EnsureProtectedProgramPath(InstalledRustDesk);
        for (var i = 0; i < 20; i++)
        {
            var result = await commandRunner.RunWithOutputAsync(InstalledRustDesk, "--get-id", cancellationToken);
            var id = result.StandardOutput.Trim();
            if (result.ExitCode == 0 && id.Length is > 0 and <= 120 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return id;
            await Task.Delay(500, cancellationToken);
        }
        return null;
    }
    public async Task<RemoteMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await serviceControl.QueryAsync(RustDeskServiceName, cancellationToken);
        return new(status is not null, string.Empty, string.Empty, status ?? "notInstalled");
    }
    internal async Task EnsureNoForeignRustDeskConfigAsync(RemoteMaintenanceConfig config, CancellationToken cancellationToken)
    {
        var status = await serviceControl.QueryAsync(RustDeskServiceName, cancellationToken);
        if (status is null) return;
        EnsureProtectedProgramPath(InstalledRustDesk);
        if (status != "running")
        {
            var marker = Path.Combine(StatusProgramDirectory, "managed-server.json");
            // 只有管理员目录中已确认接管的服务才允许为恢复事务重新启动；
            // 其余一律视为外部 RustDesk，不依据普通用户可写的 journal 绕过。
            if (!File.Exists(marker))
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged);
            EnsureProtectedProgramPath(marker);
            if (JsonSerializer.Deserialize<RemoteMaintenanceConfig>(await File.ReadAllTextAsync(marker, cancellationToken)) != config)
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged);
            if (await serviceControl.StartAsync(RustDeskServiceName, cancellationToken) != 0)
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskSetupIncomplete);
        }
        foreach (var expected in new[] { ("custom-rendezvous-server", config.IdServer), ("relay-server", config.RelayServer), ("key", config.PublicKey) })
            if (!await OptionMatchesAsync(InstalledRustDesk, expected.Item1, expected.Item2, cancellationToken))
                throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged);
    }
    internal static void ValidateConfig(RemoteMaintenanceConfig config)
    {
        if (config.IdServer != "hotbargain.vip:21116" || config.RelayServer != "hotbargain.vip:21117" ||
            Convert.FromBase64String(config.PublicKey).Length != 32)
            throw new InvalidDataException("RustDesk 配置必须指向公司服务器。");
    }
    private static string ResolveTrustedStatusAgentPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Hbpos.RemoteStatus.exe");
        EnsureProtectedProgramPath(path);
        if (!File.Exists(path)) throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.ComponentsMissing);
        return path;
    }
    internal static void EnsureProtectedProgramPath(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(ProgramRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.InstallationLocationInvalid);
        try { EnsureNoReparsePoints(path); }
        catch (InvalidDataException)
        { throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.InstallationPermissionsInvalid); }
        var writeRights = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        for (var current = Path.GetFullPath(path); current.Length >= ProgramRoot.Length; current = Path.GetDirectoryName(current)!)
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            FileSystemSecurity security;
            try
            {
                security = Directory.Exists(current)
                    ? new DirectoryInfo(current).GetAccessControl() : new FileInfo(current).GetAccessControl();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            { throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.InstallationPermissionsInvalid); }
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                    (rule.FileSystemRights & writeRights) == 0) continue;
                var sid = rule.IdentityReference.Value;
                if (sid is not ("S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"))
                    throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.InstallationPermissionsInvalid);
            }
        }
    }
    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("远程维护路径不能包含重解析点。");
    }
    private static void ApplyDirectoryAcl(string path, bool localServiceCanRead, bool localServiceCanWrite = false)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (localServiceCanRead)
            acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
                localServiceCanWrite ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute,
                inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
}
