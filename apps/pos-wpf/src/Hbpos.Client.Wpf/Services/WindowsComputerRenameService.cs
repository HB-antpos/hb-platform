using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Hbpos.Client.Wpf.Services.NetworkDiagnostics;
using Microsoft.Win32;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 真正改系统的那一步：通过 Windows 自带的 PowerShell 以管理员身份执行<b>固定模板</b>的 <c>Rename-Computer</c>。
/// 脚本里只会出现通过 <see cref="ComputerNamePolicy.IsValid"/> 校验的电脑名（字母/数字/短横线）；执行前由 Windows 弹出管理员授权窗口。
/// </summary>
public sealed class WindowsComputerRenameService : IComputerRenameService
{
    // 「配置的电脑名」改完立即写入；「生效的电脑名」要重启后才跟着变。两者不同 = 已改名待重启。
    private const string ConfiguredNameKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName";
    private const string ActiveNameKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName";
    private const string NameValue = "ComputerName";
    private const int NetSetupDomainName = 3;
    private const int RestartDelaySeconds = 20;

    public ComputerRenameState GetState()
    {
        return new ComputerRenameState(Environment.MachineName, ReadPendingName(), IsDomainJoined());
    }

    public async Task<bool> RenameAsync(string newName, CancellationToken cancellationToken)
    {
        if (!ComputerNamePolicy.IsValid(newName))
        {
            throw new ArgumentException("The computer name contains unsupported characters.", nameof(newName));
        }

        try
        {
            // 不带 -Restart：重启由用户在页面上点「立即重启」确认，避免没提示就把收银机重启。
            return await PowerShellDnsConfigurator.RunElevatedAsync(
                $"Rename-Computer -NewName '{newName}' -Force",
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // 复用的管理员脚本执行器只会报「网络修改失败」，对改名没有意义；保留原异常，换成对应的提示。
            throw new InvalidOperationException(
                "Windows refused to rename this computer (the elevated rename script failed).",
                ex);
        }
    }

    public void Restart()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = $"/r /t {RestartDelaySeconds} /c \"HB POS: restarting to apply the new computer name\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })?.Dispose();
    }

    private static string? ReadPendingName()
    {
        try
        {
            using var configured = Registry.LocalMachine.OpenSubKey(ConfiguredNameKey);
            using var active = Registry.LocalMachine.OpenSubKey(ActiveNameKey);
            var configuredName = configured?.GetValue(NameValue) as string;
            var activeName = active?.GetValue(NameValue) as string;
            return !string.IsNullOrWhiteSpace(configuredName)
                   && !string.IsNullOrWhiteSpace(activeName)
                   && !string.Equals(configuredName, activeName, StringComparison.OrdinalIgnoreCase)
                ? configuredName
                : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // 读不到就当作没有待重启的改名：最坏只是多提示一次改名，不会误改任何东西。
            return null;
        }
    }

    /// <summary>加入了域的电脑不能用本机管理员脚本改名（需要域凭据），页面据此提示联系 IT。</summary>
    private static bool IsDomainJoined()
    {
        var buffer = IntPtr.Zero;
        try
        {
            return NetGetJoinInformation(null, out buffer, out var status) == 0 && status == NetSetupDomainName;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out int joinStatus);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
