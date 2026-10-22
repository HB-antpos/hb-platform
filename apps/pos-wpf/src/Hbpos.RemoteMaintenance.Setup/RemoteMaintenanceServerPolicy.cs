namespace Hbpos.RemoteMaintenance.Setup;

/// <summary>
/// 受管 RustDesk 允许指向的公司服务器白名单。
/// 提权 helper、安装器和启动对账都用同一份规则，中心即使被篡改，也只能在这两个公司域名间切换，
/// 不能把设备指向外部服务器。
/// </summary>
public static class RemoteMaintenanceServerPolicy
{
    /// <summary>历史地址：与业务主域名共用，WPF 1.0.84 及之前只认这一个。</summary>
    public const string LegacyHost = "hotbargain.vip";

    /// <summary>RustDesk 专用域名，解析到独立的 AWS 悉尼主机。</summary>
    public const string DedicatedHost = "rd.hotbargain.vip";

    private const string IdPort = "21116";
    private const string RelayPort = "21117";

    private static readonly string[] TrustedHosts = [LegacyHost, DedicatedHost];

    /// <summary>ID 服务器与中继服务器必须是同一个白名单主机，端口固定，公钥必须是 32 字节 ed25519。</summary>
    public static bool IsTrusted(RemoteMaintenanceConfig? config)
    {
        if (config is null || string.IsNullOrEmpty(config.PublicKey)) return false;
        var matchesHost = false;
        foreach (var host in TrustedHosts)
        {
            if (string.Equals(config.IdServer, $"{host}:{IdPort}", StringComparison.Ordinal) &&
                string.Equals(config.RelayServer, $"{host}:{RelayPort}", StringComparison.Ordinal))
            {
                matchesHost = true;
                break;
            }
        }

        if (!matchesHost) return false;
        Span<byte> key = stackalloc byte[64];
        if (!Convert.TryFromBase64String(config.PublicKey, key, out var written) || written != 32) return false;
        // TryFromBase64String 会忽略空白字符；要求规范写法往返一致，带换行/空格的“公钥”不被信任。
        return string.Equals(Convert.ToBase64String(key[..32]), config.PublicKey, StringComparison.Ordinal);
    }
}
