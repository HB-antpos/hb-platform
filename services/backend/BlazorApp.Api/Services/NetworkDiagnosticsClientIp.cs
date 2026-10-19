using System.Net;
using System.Net.Sockets;

namespace BlazorApp.Api.Services;

/// <summary>
/// 网络测试诊断接口的客户端 IP 识别（公网 IP 展示 + 限流分桶）。
///
/// 为什么不能只用 <see cref="IClientIpResolver"/>：它依赖 ClientIp:TrustedProxies / TrustedNetworks 配置，
/// 生产没有配置，容器里看到的对端是 docker 网关 172.19.0.1（私网），于是几乎所有请求都被解析成 "unknown"
/// （2026-10-19 查 UserLoginDeviceRecord：近 3 天 88 条登录有 80 条是 unknown）。
/// 用它做限流分桶会让全体设备共用一个桶；用它展示公网 IP 会永远显示「未识别」。
///
/// 这里的口径：对端是公网地址 = 直连，直接用；对端是私网 / 回环（本机 nginx 或 docker 网关转发）时，
/// 采用 nginx 写入的 X-Real-IP（nginx 配置为 $remote_addr，会覆盖客户端自带的同名头）。
/// 不采用 X-Forwarded-For：nginx 用 $proxy_add_x_forwarded_for，会保留客户端伪造的前缀。
/// 同时支持 IPv4 与 IPv6（门店手机常走 IPv6）。
/// </summary>
public static class NetworkDiagnosticsClientIp
{
    public const string Unknown = "unknown";
    private const string RealIpHeader = "X-Real-IP";

    /// <summary>
    /// 客户端公网 IP；识别不出（私网对端且无有效 X-Real-IP）返回 null，用于接口展示。
    /// 优先用已有的 IClientIpResolver（配置了可信代理的环境更准），它给不出再用 X-Real-IP 兜底。
    /// </summary>
    public static string? TryResolvePublic(HttpContext context, IClientIpResolver? resolver)
    {
        var primary = resolver?.Resolve(context);
        if (!string.IsNullOrWhiteSpace(primary)
            && !string.Equals(primary, Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        var remote = Normalize(context.Connection.RemoteIpAddress);
        if (remote == null)
        {
            return null;
        }

        if (!IsPrivateOrLoopback(remote))
        {
            return remote.ToString();
        }

        var header = context.Request.Headers[RealIpHeader].ToString();
        var real = Normalize(IPAddress.TryParse(header.Trim(), out var parsed) ? parsed : null);
        return real != null && !IsPrivateOrLoopback(real) ? real.ToString() : null;
    }

    /// <summary>
    /// 限流分桶键：能识别出公网 IP 就按它分桶（每个门店出口一个桶）；
    /// 识别不出时退回对端地址（至少把「本机 / 网关」与其他来源分开），最后才是 "unknown"。
    /// </summary>
    public static string PartitionKey(HttpContext context, IClientIpResolver? resolver) =>
        TryResolvePublic(context, resolver)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? Unknown;

    private static IPAddress? Normalize(IPAddress? address)
    {
        if (address == null)
        {
            return null;
        }

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 or 10 or 127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
                192 when bytes[1] == 168 => true,
                // 100.64.0.0/10 运营商级 NAT，同样不是用户的公网出口。
                100 when bytes[1] >= 64 && bytes[1] <= 127 => true,
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Any)
                // fc00::/7 唯一本地地址
                || (bytes[0] & 0xfe) == 0xfc;
        }

        return true;
    }
}
