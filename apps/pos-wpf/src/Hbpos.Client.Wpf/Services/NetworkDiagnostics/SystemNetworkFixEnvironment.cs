using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

/// <summary>
/// 真正改系统的那一步：通过 Windows 自带的 PowerShell 以管理员身份执行<b>固定模板</b>的脚本。
/// 脚本里只会出现整数网卡编号和校验过的 IPv4 地址，不接受任何其他外部输入；执行前由 Windows 弹出管理员授权窗口。
/// </summary>
public sealed class PowerShellDnsConfigurator : IDnsConfigurator
{
    private const string TcpipInterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const int ElevationCancelledErrorCode = 1223;
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(60);

    public DnsSettings? GetCurrent(int adapterIndex)
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                var properties = adapter.GetIPProperties();
                if (properties.GetIPv4Properties()?.Index != adapterIndex)
                {
                    continue;
                }

                var servers = properties.DnsAddresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.ToString())
                    .ToArray();
                // NameServer 为空 = 由 DHCP 下发（"自动"）；有值 = 手动配置。
                using var key = Registry.LocalMachine.OpenSubKey(TcpipInterfacesKey + adapter.Id);
                var manual = key?.GetValue("NameServer") as string;
                return new DnsSettings(servers, string.IsNullOrWhiteSpace(manual));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    public Task<bool> SetServersAsync(int adapterIndex, IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        var validated = servers
            .Select(server => IPAddress.TryParse(server, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                ? ip.ToString()
                : throw new ArgumentException("Only IPv4 DNS servers are supported.", nameof(servers)))
            .ToArray();
        var list = string.Join(",", validated.Select(server => $"'{server}'"));
        return RunElevatedAsync(
            $"Set-DnsClientServerAddress -InterfaceIndex {adapterIndex} -ServerAddresses ({list})\r\nClear-DnsClientCache",
            cancellationToken);
    }

    public Task<bool> ResetToAutomaticAsync(int adapterIndex, CancellationToken cancellationToken)
    {
        return RunElevatedAsync(
            $"Set-DnsClientServerAddress -InterfaceIndex {adapterIndex} -ResetServerAddresses\r\nClear-DnsClientCache",
            cancellationToken);
    }

    private static async Task<bool> RunElevatedAsync(string body, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hbpos.Client",
            "netfix");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, $"run-{Guid.NewGuid():N}.ps1");
        // Windows PowerShell 5.1 读取不带 BOM 的脚本会按系统代码页解析，保险起见写成 UTF-8 BOM。
        await File.WriteAllTextAsync(
            scriptPath,
            "$ErrorActionPreference = 'Stop'\r\n" + body + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            cancellationToken);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Could not start PowerShell.");
            using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limited.CancelAfter(ScriptTimeout);
            await process.WaitForExitAsync(limited.Token);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The network change failed (exit code {process.ExitCode}).");
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ElevationCancelledErrorCode)
        {
            // 用户在 Windows 授权窗口里点了"否"。
            return false;
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
                // 临时脚本删不掉不影响结果，下次修复会写新文件。
            }
        }
    }
}

public sealed class RegistryProxyAutoDetectStore : IProxyAutoDetectStore
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string ValueName = "AutoDetect";
    private const int OptionSettingsChanged = 39;
    private const int OptionRefresh = 37;

    public int? Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        return key?.GetValue(ValueName) as int?;
    }

    public void Set(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
        if (value is { } number)
        {
            key.SetValue(ValueName, number, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        // 通知 WinINET / WinHTTP 重新读取代理设置；失败不影响已写入的值。
        _ = InternetSetOption(IntPtr.Zero, OptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, OptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}

public sealed class JsonNetworkFixStore(string? path = null) : INetworkFixStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hbpos.Client",
        "network-fix.json");

    public IReadOnlyList<NetworkFixRecord> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<NetworkFixRecord[]>(File.ReadAllText(_path), Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 备份文件损坏时当作没有记录，不阻断检测与新的修复。
            return [];
        }
    }

    public void Save(IReadOnlyList<NetworkFixRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (records.Count == 0)
        {
            File.Delete(_path);
            return;
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(records, Options), new UTF8Encoding(false));
    }
}
