using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Hbpos.Updater;

internal interface IInstallerProcess : IDisposable
{
    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class InstallerLaunchException(string message, bool elevationCancelled) : Exception(message)
{
    public bool ElevationCancelled { get; } = elevationCancelled;
}

/// <summary>
/// 更新会话用到的进程与文件操作；单独抽出来便于测试用假实现驱动完整流程。
/// </summary>
internal interface IUpdaterSystem
{
    Task WaitForProcessExitAsync(int processId, CancellationToken cancellationToken);

    Task<IInstallerProcess> StartInstallerAsync(string installerPath, string arguments);

    bool FileExists(string path);

    string? TryReadAllText(string path);

    void TryDeleteFile(string path);

    bool TryEnsureParentDirectory(string path);

    bool IsProcessRunning(string processName, int? excludedProcessId);

    bool TryStartApp(string exePath);

    bool TryOpenFile(string path);
}

internal sealed class WindowsUpdaterSystem : IUpdaterSystem
{
    private const int ErrorCancelled = 1223;

    public async Task WaitForProcessExitAsync(int processId, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // 进程已经退出。
            return;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // 拿不到等待句柄（例如权限不足）时退回按进程号轮询。
            }
        }

        while (IsProcessAlive(processId))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public Task<IInstallerProcess> StartInstallerAsync(string installerPath, string arguments)
    {
        // 提权确认框出现期间 ShellExecute 会一直阻塞，放到后台线程，避免更新窗口停止刷新。
        return Task.Run<IInstallerProcess>(() =>
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty
                });

                return process is null
                    ? throw new InstallerLaunchException("Process.Start returned null.", elevationCancelled: false)
                    : new InstallerProcess(process);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                throw new InstallerLaunchException(ex.Message, elevationCancelled: true);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                throw new InstallerLaunchException(ex.Message, elevationCancelled: false);
            }
        });
    }

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public string? TryReadAllText(string path)
    {
        try
        {
            // 安装器可能正在写同一个文件，按共享读打开，读不到就等下一轮。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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

    public bool TryEnsureParentDirectory(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public bool IsProcessRunning(string processName, int? excludedProcessId)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Any(process => process.Id != excludedProcessId);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public bool TryStartApp(string exePath)
    {
        return TryShellExecute(exePath);
    }

    public bool TryOpenFile(string path)
    {
        return TryShellExecute(path);
    }

    private static bool TryShellExecute(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
            });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private sealed class InstallerProcess(Process process) : IInstallerProcess
    {
        public int ExitCode => process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return process.WaitForExitAsync(cancellationToken);
        }

        public void Dispose()
        {
            process.Dispose();
        }
    }
}
