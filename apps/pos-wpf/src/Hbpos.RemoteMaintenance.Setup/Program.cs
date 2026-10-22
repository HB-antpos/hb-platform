namespace Hbpos.RemoteMaintenance.Setup;

using System.Text.Json;

/// <summary>
/// 发布包中的 UAC helper 入口。真实安装请求应通过受保护的本地 journal 传递，
/// 命令行只携带 operationId 与原用户 journal 路径，不携带密码/token。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 6 || !string.Equals(args[0], "--operation", StringComparison.Ordinal) ||
            !string.Equals(args[2], "--stage", StringComparison.Ordinal) ||
            args[3] is not ("install" or "configure" or "fail-closed" or "reconcile") ||
            !string.Equals(args[4], "--journal", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(args[5]))
        {
            return 2;
        }

        if (!Guid.TryParse(args[1], out var operationId)) return 2;
        if (!IsSafeJournalPath(args[5])) return Emit(false, args[3], operationId, 2);
        try
        {
            var journal = new RemoteMaintenanceJournal(
                args[5],
                new DpapiRemoteMaintenanceSecretProtector());
            var state = journal.ReadAsync().GetAwaiter().GetResult();
            // 对账只需要已提交的操作与待应用配置，不依赖安装包路径等安装期字段，所以先于下面的完整性检查处理。
            if (args[3] == "reconcile") return Reconcile(journal, state, operationId);
            if (state is null || state.OperationId != operationId || state.Config is null ||
                state.RustDeskArtifactPath is null || state.StatusAgentArtifactPath is null ||
                state.DataDirectory is null || state.ArtifactManifest is null)
            {
                return Emit(false, args[3], operationId, 3);
            }

            var installer = new WindowsRemoteMaintenanceInstaller(
                new WindowsRemoteMaintenanceCommandRunner(),
                new WindowsRemoteMaintenanceServiceControl(new WindowsRemoteMaintenanceCommandRunner()),
                async progress =>
                {
                    // helper 以管理员身份运行；把已实际触及的服务写回用户 journal，
                    // 使外层 fail-closed 能区分预检查拒绝与部分安装失败。
                    var current = await journal.ReadAsync(CancellationToken.None);
                    if (current is null || current.OperationId != operationId)
                        throw new InvalidOperationException("远程维护安装进度无法安全写入 journal。");
                    await journal.WriteAsync(current with
                    {
                        RustDeskInstalled = progress.RustDeskInstalled,
                        StatusAgentInstalled = progress.StatusAgentInstalled,
                        RustdeskId = string.IsNullOrWhiteSpace(progress.RustdeskId) ? current.RustdeskId : progress.RustdeskId,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    }, CancellationToken.None);
                });
            if (args[3] == "install")
            {
                var password = new DpapiRemoteMaintenanceSecretProtector().Unprotect(state.ProtectedPassword);
                if (string.IsNullOrWhiteSpace(password)) return Emit(false, "install", operationId, 3);
                var prepare = new RemoteMaintenancePrepareResponse(
                    state.OperationId,
                    state.DeviceId,
                    state.Config,
                    state.ArtifactManifest!);
                var result = installer.InstallAsync(
                    new RemoteMaintenanceInstallationRequest(
                        prepare,
                        state.RustDeskArtifactPath,
                        state.StatusAgentArtifactPath,
                        password,
                        state.RustdeskId,
                        state.ClientVersion,
                        state.DataDirectory)).GetAwaiter().GetResult();
                // InstallAsync 只在受保护 Program Files EXE 上完成 --get-id，并将实际 ID
                // 放入结果；helper 不得再次执行 journal 指向的用户可写安装包。
                var rustdeskId = result.RustdeskId;
                if (string.IsNullOrWhiteSpace(rustdeskId)) return Emit(false, "install", operationId, 4);
                journal.WriteAsync((journal.ReadAsync(CancellationToken.None).GetAwaiter().GetResult() ?? state) with
                {
                    State = RemoteMaintenanceOperationState.InstalledPendingCommit,
                    RustdeskId = rustdeskId,
                    RustDeskInstalled = result.RustDeskInstalled,
                    StatusAgentInstalled = result.StatusAgentInstalled,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                }).GetAwaiter().GetResult();
                return Emit(result.RustDeskInstalled && result.StatusAgentInstalled, "install", operationId,
                    result.RustDeskInstalled && result.StatusAgentInstalled ? 0 : 4);
            }

            if (args[3] == "fail-closed")
            {
                installer.FailClosedAsync(new RemoteMaintenanceInstallationResult(
                    state.RustDeskInstalled,
                    state.StatusAgentInstalled,
                    state.RustdeskId,
                    state.ClientVersion,
                    state.DataDirectory)).GetAwaiter().GetResult();
                return Emit(true, "fail-closed", operationId, 0);
            }

            if (state.ProtectedMonitorToken is null || state.HeartbeatUrl is null) return Emit(false, "configure", operationId, 3);
            var monitorToken = new DpapiRemoteMaintenanceSecretProtector().Unprotect(state.ProtectedMonitorToken);
            if (string.IsNullOrWhiteSpace(monitorToken)) return Emit(false, "configure", operationId, 3);
            var configurePrepare = new RemoteMaintenancePrepareResponse(
                state.OperationId,
                state.DeviceId,
                state.Config,
                state.ArtifactManifest!);

            installer.ConfigureStatusAgentAsync(
                configurePrepare,
                new RemoteMaintenanceCommitResponse(state.DeviceId, monitorToken, state.HeartbeatUrl),
                state.RustdeskId,
                state.ClientVersion).GetAwaiter().GetResult();
            return Emit(true, "configure", operationId, 0);
        }
        catch (RemoteMaintenanceSetupException ex)
        {
            return Emit(false, args[3], operationId, (int)ex.Error);
        }
        catch
        {
            // helper 输出只含阶段与退出码，绝不暴露密码、token、异常原文或命令行参数。
            return Emit(false, args[3], operationId, 4);
        }
    }

    /// <summary>
    /// 启动对账阶段：把用户态写入 journal 的 PendingConfig 应用到本机 RustDesk。
    /// PendingConfig 来自普通用户可写的 journal，所以应用前由安装器再次校验白名单、公钥与接管标记；
    /// 只有应用成功后才把它并入 Config，失败时 Config 保持旧值，下次对账会重试。
    /// </summary>
    private static int Reconcile(RemoteMaintenanceJournal journal, RemoteMaintenanceJournalState? state, Guid operationId)
    {
        if (state is null || state.OperationId != operationId ||
            state.State != RemoteMaintenanceOperationState.Committed || state.PendingConfig is null)
            return Emit(false, "reconcile", operationId, 3);

        // 手动修复与自动对账、不同 Windows 会话可能同时拉起 helper；两个提权进程交错 stop/start 服务没有意义，
        // 串行执行。拿不到锁就放弃本次，由下次对账重试。
        using var mutex = new Mutex(false, @"Global\Hbpos.RemoteMaintenance.Setup.Reconcile");
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return Emit(false, "reconcile", operationId, 4);
        try
        {
            // 对账只换服务器，不读取、不设置无人值守密码。
            var commandRunner = new WindowsRemoteMaintenanceCommandRunner();
            var installer = new WindowsRemoteMaintenanceInstaller(commandRunner, new WindowsRemoteMaintenanceServiceControl(commandRunner));
            installer.ReconcileServerAsync(state.PendingConfig).GetAwaiter().GetResult();

            var latest = journal.ReadAsync(CancellationToken.None).GetAwaiter().GetResult() ?? state;
            journal.WriteAsync(latest with
            {
                Config = state.PendingConfig,
                PendingConfig = null,
                ReconcileFailureCount = 0,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }).GetAwaiter().GetResult();
            return Emit(true, "reconcile", operationId, 0);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static int Emit(bool ok, string stage, Guid operationId, int exitCode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok, stage, operationId, exitCode }));
        return exitCode;
    }

    private static bool IsSafeJournalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(path), "remote-maintenance.journal", StringComparison.OrdinalIgnoreCase))
            return false;

        var parent = Directory.GetParent(path);
        if (parent is null || !string.Equals(parent.Name, "Hbpos.Client", StringComparison.OrdinalIgnoreCase)) return false;
        for (var current = new DirectoryInfo(parent.FullName); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }

        return File.Exists(path);
    }

}
