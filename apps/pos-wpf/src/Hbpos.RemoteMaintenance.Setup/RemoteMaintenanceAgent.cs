using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hbpos.RemoteMaintenance.Setup;

/// <summary>
/// 免 UAC 的远程维护代理请求。代理以 SYSTEM 运行、任何已登录用户都能触发，所以请求只带“要切到哪个公司服务器”，
/// 不带 journal 路径、密码或文件路径：SYSTEM 端不读写任何普通用户可控的文件，只依据 Program Files 里的接管标记执行。
/// 目前只开放 reconcile（换服务器），安装、配置、清理仍走 UAC helper，等密码改由 SYSTEM 端生成后再迁。
/// </summary>
public sealed record RemoteMaintenanceAgentRequest(string RequestId, RemoteMaintenanceConfig Config)
{
    internal const int MaxEncodedLength = 2048;
    private const string ReconcileStage = "reconcile";

    public static RemoteMaintenanceAgentRequest CreateReconcile(RemoteMaintenanceConfig config) =>
        new(Guid.NewGuid().ToString("N"), config);

    public string Encode()
    {
        var json = JsonSerializer.Serialize(new Payload(1, RequestId, ReconcileStage, Config.IdServer, Config.RelayServer, Config.PublicKey));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>逐项按不可信输入校验：请求号必须是 GUID，服务器必须在公司白名单内。</summary>
    public static bool TryDecode(string? encoded, out RemoteMaintenanceAgentRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxEncodedLength) return false;
        Payload? payload;
        try
        {
            var base64 = encoded.Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(base64));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        if (payload is not { Version: 1, Stage: ReconcileStage } ||
            !Guid.TryParseExact(payload.Id, "N", out var id) ||
            payload.IdServer is null || payload.RelayServer is null || payload.PublicKey is null)
            return false;
        var config = new RemoteMaintenanceConfig(payload.IdServer, payload.RelayServer, payload.PublicKey);
        if (!RemoteMaintenanceServerPolicy.IsTrusted(config)) return false;
        request = new RemoteMaintenanceAgentRequest(id.ToString("N"), config);
        return true;
    }

    private sealed record Payload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("stage")] string? Stage,
        [property: JsonPropertyName("idServer")] string? IdServer,
        [property: JsonPropertyName("relayServer")] string? RelayServer,
        [property: JsonPropertyName("key")] string? PublicKey);
}

/// <summary>代理写给调用方的结果；Completed 为 false 表示已受理、仍在执行。</summary>
public sealed record RemoteMaintenanceAgentStatus(
    [property: JsonPropertyName("id")] string RequestId,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAtUtc)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static RemoteMaintenanceAgentStatus? TryParse(string? json, string expectedRequestId)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var status = JsonSerializer.Deserialize<RemoteMaintenanceAgentStatus>(json);
            return status is not null && string.Equals(status.RequestId, expectedRequestId, StringComparison.Ordinal) ? status : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// 代理的状态目录固定在 helper 所在的安装目录（Program Files 下）：SYSTEM 可写、普通用户只读，
/// 调用方读到的结果不可能被本机其他程序伪造。
/// </summary>
public static class RemoteMaintenanceAgentLayout
{
    public const string DirectoryName = "RemoteMaintenanceAgent";

    public static string StatusDirectory(string helperPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(helperPath))!, DirectoryName, "status");

    public static string StatusFile(string helperPath, string requestId) =>
        Path.Combine(StatusDirectory(helperPath), requestId + ".json");
}

public interface IRemoteMaintenanceAgentTaskScheduler
{
    /// <summary>返回已注册代理任务要运行的程序路径；未注册、已禁用或无权读取时返回 null。</summary>
    string? GetRegisteredAgentPath();

    bool TryRun(string encodedRequest);

    void Register(string helperPath);

    void Unregister();
}

/// <summary>
/// 通过 Windows 任务计划（COM）管理“HB POS Remote Maintenance Agent”：安装包以管理员身份注册，任务以 SYSTEM 运行、
/// 动作路径固定为受保护安装目录里的 helper；已登录用户只有读取和运行权限，运行时只能传入一条请求。
/// 做法与 Hbpos.Updater 的 WindowsUpdateAgentTaskScheduler 相同，两者各管各的任务，互不影响。
/// </summary>
public sealed class WindowsRemoteMaintenanceAgentTaskScheduler : IRemoteMaintenanceAgentTaskScheduler
{
    public const string TaskName = "HB POS Remote Maintenance Agent";
    public const string AgentArguments = RemoteMaintenanceAgent.AgentOption + " $(Arg0)";

    // SYSTEM、Administrators 完全控制；已登录用户（AU）只能读取和运行，不能修改动作或删除任务。
    internal const string TaskSecurityDescriptor = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)";

    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonServiceAccount = 5;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;

    public string? GetRegisteredAgentPath()
    {
        try
        {
            dynamic task = GetRootFolder().GetTask(TaskName);
            if (!(bool)task.Enabled) return null;
            dynamic actions = task.Definition.Actions;
            if ((int)actions.Count < 1) return null;
            dynamic action = actions.Item(1);
            return (int)action.Type == TaskActionExec ? (string)action.Path : null;
        }
        catch (Exception ex) when (IsTaskSchedulerFailure(ex))
        {
            return null;
        }
    }

    public bool TryRun(string encodedRequest)
    {
        try
        {
            dynamic task = GetRootFolder().GetTask(TaskName);
            task.Run(encodedRequest);
            return true;
        }
        catch (Exception ex) when (IsTaskSchedulerFailure(ex))
        {
            return false;
        }
    }

    public void Register(string helperPath)
    {
        dynamic service = ConnectService();
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Author = "Hot Bargain";
        definition.RegistrationInfo.Description = "HB POS 远程维护代理：以系统权限把受管 RustDesk 切换到公司服务器，避免收银机弹出 UAC 确认框。";
        definition.Principal.UserId = "S-1-5-18";
        definition.Principal.LogonType = TaskLogonServiceAccount;
        definition.Principal.RunLevel = TaskRunLevelHighest;
        definition.Settings.Enabled = true;
        definition.Settings.Hidden = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.StartWhenAvailable = false;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;
        // 对账最多等 2 分钟互斥锁，再加停启服务与读回；15 分钟足够，到点会连同子进程一起终止。
        definition.Settings.ExecutionTimeLimit = "PT15M";

        dynamic action = definition.Actions.Create(TaskActionExec);
        action.Path = helperPath;
        action.Arguments = AgentArguments;
        action.WorkingDirectory = Path.GetDirectoryName(helperPath) ?? string.Empty;

        GetRootFolder(service).RegisterTaskDefinition(
            TaskName,
            definition,
            TaskCreateOrUpdate,
            "SYSTEM",
            null,
            TaskLogonServiceAccount,
            TaskSecurityDescriptor);
    }

    public void Unregister()
    {
        try
        {
            GetRootFolder().DeleteTask(TaskName, 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 任务本来就不存在时视为已卸载。
        }
    }

    private static dynamic ConnectService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(serviceType)!;
        service.Connect();
        return service;
    }

    private static dynamic GetRootFolder(dynamic? service = null) => (service ?? ConnectService()).GetFolder("\\");

    private static bool IsTaskSchedulerFailure(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException
            or InvalidCastException or ArgumentException or NotSupportedException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;
}

/// <summary>
/// SYSTEM 端：任务计划以 --agent 拉起 helper，校验请求后在本进程内执行对账，把退出码写进状态目录。
/// 安全依据只有 Program Files 里的接管标记与 <see cref="WindowsRemoteMaintenanceInstaller.ReconcileServerAsync"/> 的全部校验，
/// 恶意调用最多让受管 RustDesk 在两个公司地址间切换并重启一次服务。
/// </summary>
public sealed class RemoteMaintenanceAgent(
    string helperPath,
    Func<RemoteMaintenanceConfig, CancellationToken, Task> reconcile,
    TimeProvider? timeProvider = null)
{
    public const string AgentOption = "--agent";
    public const string RegisterOption = "--register-agent-task";
    public const string UnregisterOption = "--unregister-agent-task";

    internal const int InvalidRequestExitCode = 2;
    internal const int FailureExitCode = 4;
    internal const int NotProtectedExitCode = 5;
    internal const int RegistrationFailedExitCode = 6;

    // 与 UAC helper 的 reconcile 阶段共用同一把全局锁：手动修复与自动对账、不同 Windows 会话不会交错停启服务。
    internal const string ReconcileMutexName = @"Global\Hbpos.RemoteMaintenance.Setup.Reconcile";
    internal TimeSpan MutexTimeout { get; init; } = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan StatusRetention = TimeSpan.FromDays(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 同步执行：全局 Mutex 必须由获取它的线程释放，await 之后可能换线程，所以与 UAC helper 一样阻塞等待对账完成。
    /// </summary>
    public int Run(string? encodedRequest)
    {
        if (!RemoteMaintenanceAgentRequest.TryDecode(encodedRequest, out var request) || request is null)
            return InvalidRequestExitCode;

        var statusDirectory = RemoteMaintenanceAgentLayout.StatusDirectory(helperPath);
        Directory.CreateDirectory(statusDirectory);
        CleanupStaleStatusFiles(statusDirectory);
        WriteStatus(request.RequestId, completed: false, exitCode: 0);

        int exitCode;
        using (var mutex = new Mutex(false, ReconcileMutexName))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(MutexTimeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                // 另一个对账正在执行；本次放弃，由下次对账或手动修复重试。
                exitCode = FailureExitCode;
            }
            else
            {
                try
                {
                    reconcile(request.Config, CancellationToken.None).GetAwaiter().GetResult();
                    exitCode = 0;
                }
                catch (RemoteMaintenanceSetupException ex) { exitCode = (int)ex.Error; }
                // 只返回固定退出码，不把异常原文写进普通用户可读的状态文件。
                catch (Exception) { exitCode = FailureExitCode; }
                finally { mutex.ReleaseMutex(); }
            }
        }

        WriteStatus(request.RequestId, completed: true, exitCode);
        return exitCode;
    }

    // 临时文件加原子替换：调用方轮询时要么读到旧状态要么读到新状态，不会读到半截 JSON。
    private void WriteStatus(string requestId, bool completed, int exitCode)
    {
        var path = RemoteMaintenanceAgentLayout.StatusFile(helperPath, requestId);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, new RemoteMaintenanceAgentStatus(requestId, completed, exitCode, _timeProvider.GetUtcNow()).Serialize());
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void CleanupStaleStatusFiles(string statusDirectory)
    {
        var cutoff = _timeProvider.GetUtcNow() - StatusRetention;
        foreach (var file in Directory.EnumerateFiles(statusDirectory))
        {
            try { if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>普通用户端：把对账交给 SYSTEM 代理执行，并从受保护的状态目录读取结果。</summary>
public interface IRemoteMaintenanceAgentClient
{
    /// <summary>代理任务已注册且动作正是本机这份受保护的 helper 时才可用；否则调用方退回 UAC helper。</summary>
    bool IsAvailable(string helperPath);

    /// <summary>返回 helper 同款退出码；代理没有受理请求时返回 <see cref="RemoteMaintenanceAgentClient.NotRespondingExitCode"/>。</summary>
    Task<int> ReconcileAsync(string helperPath, RemoteMaintenanceConfig desired, CancellationToken cancellationToken = default);
}

public sealed class RemoteMaintenanceAgentClient(
    IRemoteMaintenanceAgentTaskScheduler scheduler,
    TimeProvider? timeProvider = null) : IRemoteMaintenanceAgentClient
{
    public const int NotRespondingExitCode = -2;

    // 任务计划正常几秒内就会写“已受理”；超过这个时间多半是任务被删、被禁用或另一个请求正在执行。
    internal TimeSpan AcceptTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool IsAvailable(string helperPath)
    {
        var registered = scheduler.GetRegisteredAgentPath();
        return registered is not null &&
            string.Equals(Path.GetFullPath(registered), Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<int> ReconcileAsync(string helperPath, RemoteMaintenanceConfig desired, CancellationToken cancellationToken = default)
    {
        var request = RemoteMaintenanceAgentRequest.CreateReconcile(desired);
        if (!scheduler.TryRun(request.Encode())) return NotRespondingExitCode;

        var statusFile = RemoteMaintenanceAgentLayout.StatusFile(helperPath, request.RequestId);
        var startedAt = _timeProvider.GetTimestamp();
        var accepted = false;
        while (true)
        {
            var status = RemoteMaintenanceAgentStatus.TryParse(TryReadAllText(statusFile), request.RequestId);
            if (status is not null)
            {
                if (status.Completed) return status.ExitCode;
                accepted = true;
            }

            // 已受理后不再自己计时，交给调用方的总超时（对账为 5 分钟）。
            if (!accepted && _timeProvider.GetElapsedTime(startedAt) >= AcceptTimeout) return NotRespondingExitCode;
            await Task.Delay(PollInterval, _timeProvider, cancellationToken);
        }
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
