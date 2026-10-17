using System.Globalization;
using System.Text;

namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

public enum DiagnosticStepId
{
    LocalNetwork,
    SystemDns,
    PublicDns,
    ConnectIpv4,
    ConnectIpv6,
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
            builder.AppendLine($"{step.Id,-14} {step.Status,-8} {elapsed,8}  {step.Detail}");
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
