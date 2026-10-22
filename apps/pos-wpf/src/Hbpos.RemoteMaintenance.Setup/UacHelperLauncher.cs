using System.Diagnostics;

namespace Hbpos.RemoteMaintenance.Setup;

public interface IRemoteMaintenanceUacHelperLauncher
{
    void ValidateInstallation(string helperPath);
    Task<int> RunAsync(string helperPath, string journalPath, Guid operationId, string stage, CancellationToken cancellationToken = default);
}

public sealed class WindowsRemoteMaintenanceUacHelperLauncher : IRemoteMaintenanceUacHelperLauncher
{
    public void ValidateInstallation(string helperPath)
    {
        // 下载前只读检查正式安装来源；下载到用户目录的 Agent 不能代替受保护的随包组件。
        ValidateHelper(helperPath);
        var agentPath = Path.Combine(Path.GetDirectoryName(helperPath)!, "Hbpos.RemoteStatus.exe");
        if (!File.Exists(agentPath))
            throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.ComponentsMissing);
        WindowsRemoteMaintenanceInstaller.EnsureProtectedProgramPath(agentPath);
    }

    private static void ValidateHelper(string helperPath)
    {
        if (!Path.IsPathFullyQualified(helperPath))
            throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.InstallationLocationInvalid);
        WindowsRemoteMaintenanceInstaller.EnsureProtectedProgramPath(helperPath);
        if (!File.Exists(helperPath))
            throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.ComponentsMissing);
    }

    public async Task<int> RunAsync(string helperPath, string journalPath, Guid operationId, string stage, CancellationToken cancellationToken = default)
    {
        if (stage is not ("install" or "configure" or "fail-closed" or "reconcile")) throw new ArgumentException("无效的 helper 阶段。", nameof(stage));
        if (!Path.IsPathFullyQualified(journalPath)) throw new ArgumentException("journal 路径必须是绝对路径。", nameof(journalPath));
        // 所有提权入口都必须来自受保护安装目录；恢复与清理不要求重新安装状态组件。
        if (stage == "install") ValidateInstallation(helperPath);
        else ValidateHelper(helperPath);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                // 传入原始用户 profile 的 journal，避免 runas 后切换到另一管理员的 LocalAppData。
                // journal 路径不是秘密；密码仍只在 machine-DPAPI journal 中保存。
                Arguments = $"--operation {operationId:D} --stage {stage} --journal {Quote(journalPath)}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        // ShellExecute 可能等待 UAC 决策；放到工作线程，保持下载完成提示与界面可刷新。
        await Task.Run(() => process.Start(), cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
