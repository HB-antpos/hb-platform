using System.IO;

namespace Hbpos.Updater;

/// <summary>
/// 更新程序的无界面命令：安装包注册/注销代理任务，以及任务计划以 SYSTEM 拉起的代理与 worker。
/// 这些命令不创建任何窗口，执行完就以退出码结束进程。
/// </summary>
internal static class UpdateAgentEntryPoint
{
    internal const int NotUnderProgramFilesExitCode = 5;
    internal const int RegistrationFailedExitCode = 6;

    public static bool TryStart(IReadOnlyList<string> args, out Func<Task<int>> run)
    {
        run = null!;
        if (args.Count == 0)
        {
            return false;
        }

        var command = args[0];
        var argument = args.Count > 1 ? args[1] : null;
        var baseDirectory = AppContext.BaseDirectory;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        switch (command)
        {
            case UpdateAgent.RegisterOption:
                run = () => Task.FromResult(Register(baseDirectory, programFiles, new WindowsUpdateAgentTaskScheduler()));
                return true;
            case UpdateAgent.UnregisterOption:
                run = () => Task.FromResult(Unregister(new WindowsUpdateAgentTaskScheduler()));
                return true;
            case UpdateAgent.AgentOption:
                run = () => Task.FromResult(TryCreateAgent(baseDirectory, programFiles, worker: false, out var agent, out var layout)
                    ? agent.Dispatch(argument, layout.UpdaterDirectory)
                    : NotUnderProgramFilesExitCode);
                return true;
            case UpdateAgent.WorkerOption:
                run = () => TryCreateAgent(baseDirectory, programFiles, worker: true, out var agent, out _)
                    ? agent.RunWorkerAsync(argument, baseDirectory, CancellationToken.None)
                    : Task.FromResult(NotUnderProgramFilesExitCode);
                return true;
            default:
                return false;
        }
    }

    internal static int Register(string baseDirectory, string programFiles, IUpdateAgentTaskScheduler scheduler)
    {
        try
        {
            if (!UpdateAgentLayout.TryFromAgentBaseDirectory(baseDirectory, worker: false, out var layout) ||
                !layout.IsUnderProgramFiles(programFiles))
            {
                // 中文注释：不在 Program Files 下的安装目录普通用户可能可写，不能让 SYSTEM 任务运行那里的程序；顺手清掉旧任务。
                scheduler.Unregister();
                return NotUnderProgramFilesExitCode;
            }

            scheduler.Register(layout.UpdaterExecutablePath);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // 中文注释：注册失败只影响“免 UAC 安装”，不能让安装包本身失败；白天仍可走原来的弹框安装。
            return RegistrationFailedExitCode;
        }
    }

    internal static int Unregister(IUpdateAgentTaskScheduler scheduler)
    {
        try
        {
            scheduler.Unregister();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return RegistrationFailedExitCode;
        }
    }

    private static bool TryCreateAgent(
        string baseDirectory,
        string programFiles,
        bool worker,
        out UpdateAgent agent,
        out UpdateAgentLayout layout)
    {
        agent = null!;
        if (!UpdateAgentLayout.TryFromAgentBaseDirectory(baseDirectory, worker, out layout) ||
            !layout.IsUnderProgramFiles(programFiles))
        {
            return false;
        }

        agent = new UpdateAgent(new WindowsUpdateAgentHost(), layout, TimeProvider.System);
        return true;
    }
}
