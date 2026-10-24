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

    /// <summary>EdgeOne 入口（代号 edge）：离澳洲门店近、绕开个别运营商到新加坡的不稳线路，按请求量计费。</summary>
    public const string EdgeHost = "pos.hotbargain.vip";

    /// <summary>AWS 悉尼边缘机入口（代号 aws）：与腾讯网络完全独立，回源到同一个新加坡后端。</summary>
    public const string AwsHost = "pos-syd.hotbargain.vip";

    /// <summary>
    /// 别名组：三个域名都回源到同一个后端实例，互为备份。入口名单写死在客户端（凭据只会发往编译期确定的域名），
    /// 后台只下发「首选哪个」（见 <see cref="ApiEntryModes"/>）。规范入口（直连）同时是设备身份的计算基准。
    /// </summary>
    private static readonly string[] GroupHosts = [EdgeHost, AwsHost, CanonicalHost];

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
    /// 候选传输地址（未设置首选时按 edge 首选）。不在别名组内时只有它自己，行为与别名改造前完全一致。
    /// </summary>
    public static IReadOnlyList<Uri> GetCandidates(Uri configured) => GetCandidates(configured, preferredMode: null);

    /// <summary>
    /// 按「首选入口 + 固定优先级 aws → edge → direct」排好序的候选传输地址（规则见 <see cref="ApiEntryModes.GetOrder"/>）。
    /// 首项是首选入口，与 configured 具体是哪个别名无关：后台下发的首选才是入口顺序的唯一依据，
    /// configured 只提供协议、端口与路径。preferredMode 为 null 或不认识时按 edge。
    /// 不在别名组内时只有 configured 自己。
    /// </summary>
    public static IReadOnlyList<Uri> GetCandidates(Uri configured, string? preferredMode)
    {
        if (!IsAlias(configured))
        {
            return [configured];
        }

        var candidates = new List<Uri>(GroupHosts.Length);
        foreach (var mode in ApiEntryModes.GetOrder(preferredMode))
        {
            candidates.Add(new UriBuilder(configured) { Host = ApiEntryModes.HostOf(mode) }.Uri);
        }

        return candidates;
    }
}

/// <summary>
/// 后台为每家分店设置的「首选入口」取值与入口顺序规则（纯函数，便于单测）。
/// edge＝EdgeOne（pos.hotbargain.vip），aws＝AWS 悉尼边缘（pos-syd.hotbargain.vip），direct＝直连（hotbargain.vip）。
/// </summary>
internal static class ApiEntryModes
{
    public const string Edge = "edge";
    public const string Aws = "aws";
    public const string Direct = "direct";

    /// <summary>后台未设置（null）时的默认首选。</summary>
    public const string Default = Edge;

    /// <summary>
    /// 备用顺序的固定优先级：aws → edge → direct。理由：EdgeOne 出问题先换同样绕开坏线路的 AWS；
    /// 直连坏了先走 AWS（基本不花钱），最后才用按请求计费的 EdgeOne。
    /// </summary>
    private static readonly string[] FallbackPriority = [Aws, Edge, Direct];

    /// <summary>
    /// 解析后台下发的取值（忽略首尾空白与大小写）。认识 edge / aws / direct 时返回规范小写值；
    /// null、空白或不认识的字符串返回 false。
    /// </summary>
    public static bool TryParse(string? raw, out string mode)
    {
        mode = string.Empty;
        var trimmed = raw?.Trim();
        foreach (var known in FallbackPriority)
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase))
            {
                mode = known;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 入口顺序＝首选 + 其余按固定优先级（aws → edge → direct）排列：
    /// edge→[edge,aws,direct]；aws→[aws,edge,direct]；direct→[direct,aws,edge]。
    /// null 或不认识的取值按默认首选 edge。
    /// </summary>
    public static IReadOnlyList<string> GetOrder(string? preferredMode)
    {
        var preferred = TryParse(preferredMode, out var parsed) ? parsed : Default;
        var order = new List<string>(FallbackPriority.Length) { preferred };
        order.AddRange(FallbackPriority.Where(mode => !string.Equals(mode, preferred, StringComparison.Ordinal)));
        return order;
    }

    public static string HostOf(string mode)
    {
        return mode switch
        {
            Edge => ApiEndpointAliases.EdgeHost,
            Aws => ApiEndpointAliases.AwsHost,
            Direct => ApiEndpointAliases.CanonicalHost,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的入口代号。")
        };
    }

    /// <summary>主机名对应的入口代号；不属于别名组返回 null。</summary>
    public static string? ModeOfHost(string host)
    {
        foreach (var mode in FallbackPriority)
        {
            if (string.Equals(HostOf(mode), host, StringComparison.OrdinalIgnoreCase))
            {
                return mode;
            }
        }

        return null;
    }
}
