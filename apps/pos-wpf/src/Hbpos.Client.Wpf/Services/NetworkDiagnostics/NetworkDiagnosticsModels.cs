using System.Globalization;
using System.Text;

namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

public enum DiagnosticStepId
{
    LocalNetwork,
    /// <summary>到路由器（网关）的丢包与延迟：区分"本机无线 / 网线链路不稳"。</summary>
    GatewayLink,
    SystemDns,
    PublicDns,
    ConnectIpv4,
    ConnectIpv6,
    /// <summary>到一个公共参照地址 443 端口的连接：区分"整条链路差"与"只有服务器这个地址被挡"。</summary>
    ReferenceConnect,
    Proxy,
    SecureReply,
    Clock
}

public enum DiagnosticStepStatus
{
    Ok,
    Slow,
    Failed,
    Skipped
}

public enum DiagnosisKind
{
    /// <summary>所有检测项都正常。</summary>
    None,
    NoLocalNetwork,
    /// <summary>到路由器就丢包或很慢：本机无线 / 网线链路不稳。</summary>
    LocalLinkUnstable,
    DnsSlow,
    DnsFailed,
    DnsMismatch,
    Ipv6Slow,
    ProxyAutoDetectSlow,
    ProxyUnreachable,
    ClockSkew,
    SecureConnectionFailed,
    ServerError,
    NotPosApi,
    /// <summary>路由器没问题，但连公共参照地址也慢或失败：路由器之后的互联网出口有问题。</summary>
    InternetUnreachable,
    /// <summary>参照地址很快，只有服务器地址连不上或很慢：到服务器的路径被防火墙 / 路由 / 运营商挡了或丢包。</summary>
    ServerPathBlocked,
    ServerUnreachable
}

/// <summary>可由用户一键执行的修复。只列"安全、可还原"的几类；其余原因只提示不自动改。</summary>
public enum NetworkFixKind
{
    PublicDns,
    DisableProxyAutoDetect
}

public sealed record DiagnosticStep(
    DiagnosticStepId Id,
    DiagnosticStepStatus Status,
    long? ElapsedMs,
    string Detail);

public sealed record DiagnosisFinding(
    DiagnosisKind Kind,
    NetworkFixKind? SuggestedFix);

/// <summary>规则输入：各探测步骤实测到的原始数据（无法测得的留 null）。</summary>
public sealed record NetworkMeasurements
{
    public bool HasLocalNetwork { get; init; } = true;

    public string? AdapterName { get; init; }

    public string? Gateway { get; init; }

    public IReadOnlyList<string> SystemDnsServers { get; init; } = [];

    public long? SystemDnsMs { get; init; }

    public IReadOnlyList<string> SystemDnsAddresses { get; init; } = [];

    public bool SystemDnsFailed { get; init; }

    public long? PublicDnsMs { get; init; }

    public IReadOnlyList<string> PublicDnsAddresses { get; init; } = [];

    public bool PublicDnsFailed { get; init; }

    public long? Ipv4ConnectMs { get; init; }

    public bool Ipv4ConnectFailed { get; init; }

    public bool HasIpv6Address { get; init; }

    public long? Ipv6ConnectMs { get; init; }

    public bool Ipv6ConnectFailed { get; init; }

    /// <summary>发给网关的 ping 次数；0 或 null 表示没有测。</summary>
    public int? GatewayPingSent { get; init; }

    public int? GatewayPingLost { get; init; }

    public long? GatewayAvgMs { get; init; }

    public long? GatewayMaxMs { get; init; }

    /// <summary>参照地址（公共服务）443 端口的 TCP 连接耗时与是否失败。</summary>
    public string? ReferenceHost { get; init; }

    public long? ReferenceConnectMs { get; init; }

    public bool ReferenceConnectFailed { get; init; }

    public bool ProxyConfigured { get; init; }

    public string? ProxyAddress { get; init; }

    public bool ProxyAutoDetectEnabled { get; init; }

    /// <summary>不走任何代理的 HTTPS 健康检查耗时。</summary>
    public long? DirectReplyMs { get; init; }

    public bool DirectReplyFailed { get; init; }

    /// <summary>走系统默认代理设置的 HTTPS 健康检查耗时。</summary>
    public long? SystemProxyReplyMs { get; init; }

    public bool SystemProxyReplyFailed { get; init; }

    public ReplyOutcome Reply { get; init; } = ReplyOutcome.Unknown;

    public int? HttpStatus { get; init; }

    public TimeSpan? ClockSkew { get; init; }
}

public enum ReplyOutcome
{
    Unknown,
    Online,
    SecureConnectionFailed,
    ServerError,
    NotPosApi,
    Timeout,
    Unreachable
}

public sealed record NetworkDiagnosisReport(
    string Target,
    DateTimeOffset At,
    TimeSpan Total,
    IReadOnlyList<DiagnosticStep> Steps,
    DiagnosisFinding Finding,
    NetworkMeasurements Measurements,
    string SystemInfo)
{
    public bool HasSuggestedFix => Finding.SuggestedFix is not null;

    /// <summary>
    /// 完整的纯文本报告：给"Copy report"用，可直接发给技术支持。只含网络信息与耗时，不含收银员、条码或授权码。
    /// </summary>
    public string ToReportText()
    {
        var inv = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();
        builder.AppendLine("HB POS network report");
        builder.AppendLine($"Target   : {Target}");
        builder.AppendLine($"Time     : {At.ToString("yyyy-MM-dd HH:mm:ss zzz", inv)}");
        builder.AppendLine($"Duration : {Total.TotalSeconds.ToString("0.0", inv)} s");
        builder.AppendLine($"System   : {SystemInfo}");
        builder.AppendLine($"Finding  : {Finding.Kind}{(Finding.SuggestedFix is { } fix ? $" (suggested fix: {fix})" : string.Empty)}");
        builder.AppendLine();
        foreach (var step in Steps)
        {
            var elapsed = step.ElapsedMs is { } ms ? FormatMs(ms) : "-";
            builder.AppendLine($"{step.Id,-17} {step.Status,-8} {elapsed,8}  {step.Detail}");
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatMs(long milliseconds)
    {
        return milliseconds >= 1000
            ? (milliseconds / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " s"
            : milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
    }
}
