namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 同一套 POS API 的多个访问入口（域名别名）。
/// 关键约定：
/// 1. 「身份」（本地库分区、激活恢复数据、"是否同一台服务器"的判断）一律按规范入口计算，
///    别名与规范入口视为同一台服务器，否则切换入口会让本机丢失已激活状态并重新下载全部数据。
/// 2. 「传输」（实际把请求发到哪个域名）可以在别名之间切换，用于单条线路不稳定时的故障转移。
/// 别名必须指向同一个后端实例；把不同环境的域名放进来会串数据，只允许在这里登记生产入口。
/// </summary>
internal static class ApiEndpointAliases
{
    /// <summary>规范入口：历史上所有设备都是以它建立的本地库与激活状态。</summary>
    public const string CanonicalHost = "hotbargain.vip";

    /// <summary>
    /// 别名组，靠前的优先作为首选。pos 入口走 EdgeOne（离澳洲门店近、绕开个别运营商到新加坡的不稳线路），
    /// 规范入口直连新加坡源站，二者互为备份。
    /// </summary>
    private static readonly string[] GroupHosts = ["pos.hotbargain.vip", CanonicalHost];

    /// <summary>只有这个路径前缀的地址才属于别名组（与 POS API 的反代前缀一致）。</summary>
    private const string PosApiPath = "/pos-api/";

    public static bool IsAlias(Uri address)
    {
        return address.IsAbsoluteUri
               && string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && address.IsDefaultPort
               && string.Equals(address.AbsolutePath, PosApiPath, StringComparison.Ordinal)
               && GroupHosts.Any(host => string.Equals(host, address.IdnHost, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把别名折算成规范入口；不在别名组内的地址原样返回。</summary>
    public static Uri Canonicalize(Uri address)
    {
        if (!IsAlias(address))
        {
            return address;
        }

        return new UriBuilder(address) { Host = CanonicalHost }.Uri;
    }

    /// <summary>身份是否相同（规范化后逐字节相等）。</summary>
    public static bool SameIdentity(Uri left, Uri right)
    {
        return string.Equals(
            Canonicalize(left).AbsoluteUri,
            Canonicalize(right).AbsoluteUri,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 返回包含自身在内的候选传输地址：首项永远是传入的地址（用户/默认配置的首选），
    /// 其后是同组的备用入口。不在别名组内时只有它自己，行为与改动前完全一致。
    /// </summary>
    public static IReadOnlyList<Uri> GetCandidates(Uri configured)
    {
        if (!IsAlias(configured))
        {
            return [configured];
        }

        var candidates = new List<Uri> { configured };
        foreach (var host in GroupHosts)
        {
            if (string.Equals(host, configured.IdnHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(new UriBuilder(configured) { Host = host }.Uri);
        }

        return candidates;
    }
}
