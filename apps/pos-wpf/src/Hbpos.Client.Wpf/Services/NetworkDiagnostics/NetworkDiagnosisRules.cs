namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

/// <summary>
/// 把实测数据变成"逐项状态 + 结论"。纯函数，不碰网络和系统设置，阈值与判断全部在这里，方便单元测试。
/// 客户端对设备 API 的请求只给几秒，所以"慢"按 1.5 秒算：超过它就会表现为 Offline。
/// </summary>
public static class NetworkDiagnosisRules
{
    public const long SlowThresholdMs = 1500;
    public const long FastThresholdMs = 500;

    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<DiagnosticStep> BuildSteps(NetworkMeasurements m)
    {
        return
        [
            LocalNetworkStep(m),
            SystemDnsStep(m),
            PublicDnsStep(m),
            ConnectStep(DiagnosticStepId.ConnectIpv4, true, m.Ipv4ConnectMs, m.Ipv4ConnectFailed, "no IPv4 address returned"),
            ConnectStep(DiagnosticStepId.ConnectIpv6, m.HasIpv6Address, m.Ipv6ConnectMs, m.Ipv6ConnectFailed, "no IPv6 address returned"),
            ProxyStep(m),
            SecureReplyStep(m),
            ClockStep(m)
        ];
    }

    public static DiagnosisFinding Evaluate(NetworkMeasurements m)
    {
        if (!m.HasLocalNetwork)
        {
            return new DiagnosisFinding(DiagnosisKind.NoLocalNetwork, null);
        }

        var publicDnsFast = !m.PublicDnsFailed && m.PublicDnsMs is { } publicMs && publicMs < FastThresholdMs;

        if (m.SystemDnsFailed && !m.PublicDnsFailed)
        {
            return new DiagnosisFinding(DiagnosisKind.DnsFailed, NetworkFixKind.PublicDns);
        }

        if (!m.SystemDnsFailed && !m.PublicDnsFailed && m.Reply != ReplyOutcome.Online && AddressesDisjoint(m))
        {
            return new DiagnosisFinding(DiagnosisKind.DnsMismatch, NetworkFixKind.PublicDns);
        }

        if (!m.SystemDnsFailed && m.SystemDnsMs is { } systemMs && systemMs >= SlowThresholdMs && publicDnsFast)
        {
            return new DiagnosisFinding(DiagnosisKind.DnsSlow, NetworkFixKind.PublicDns);
        }

        if (SystemProxyPathFailed(m) && m.ProxyConfigured && !string.IsNullOrWhiteSpace(m.ProxyAddress))
        {
            // 代理可能是店里故意配置的，不自动改，只提示。
            return new DiagnosisFinding(DiagnosisKind.ProxyUnreachable, null);
        }

        if (SystemProxyPathSlow(m) && m.ProxyAutoDetectEnabled)
        {
            return new DiagnosisFinding(DiagnosisKind.ProxyAutoDetectSlow, NetworkFixKind.DisableProxyAutoDetect);
        }

        if (m.HasIpv6Address
            && (m.Ipv6ConnectFailed || m.Ipv6ConnectMs is { } v6 && v6 >= SlowThresholdMs)
            && !m.Ipv4ConnectFailed
            && m.Ipv4ConnectMs is { } v4 && v4 < FastThresholdMs)
        {
            return new DiagnosisFinding(DiagnosisKind.Ipv6Slow, null);
        }

        if (m.ClockSkew is { } skew && skew.Duration() > MaxClockSkew)
        {
            return new DiagnosisFinding(DiagnosisKind.ClockSkew, null);
        }

        return m.Reply switch
        {
            ReplyOutcome.SecureConnectionFailed => new DiagnosisFinding(DiagnosisKind.SecureConnectionFailed, null),
            ReplyOutcome.ServerError => new DiagnosisFinding(DiagnosisKind.ServerError, null),
            ReplyOutcome.NotPosApi => new DiagnosisFinding(DiagnosisKind.NotPosApi, null),
            ReplyOutcome.Timeout or ReplyOutcome.Unreachable => new DiagnosisFinding(DiagnosisKind.ServerUnreachable, null),
            _ => new DiagnosisFinding(DiagnosisKind.None, null)
        };
    }

    private static bool AddressesDisjoint(NetworkMeasurements m)
    {
        if (m.SystemDnsAddresses.Count == 0 || m.PublicDnsAddresses.Count == 0)
        {
            return false;
        }

        return !m.SystemDnsAddresses.Intersect(m.PublicDnsAddresses, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static bool SystemProxyPathFailed(NetworkMeasurements m)
    {
        return m.SystemProxyReplyFailed && !m.DirectReplyFailed;
    }

    private static bool SystemProxyPathSlow(NetworkMeasurements m)
    {
        return !m.SystemProxyReplyFailed
            && !m.DirectReplyFailed
            && m.SystemProxyReplyMs is { } viaSystem
            && m.DirectReplyMs is { } direct
            && viaSystem - direct >= SlowThresholdMs;
    }

    private static DiagnosticStep LocalNetworkStep(NetworkMeasurements m)
    {
        if (!m.HasLocalNetwork)
        {
            return new DiagnosticStep(DiagnosticStepId.LocalNetwork, DiagnosticStepStatus.Failed, null, "no active network adapter with a gateway");
        }

        var detail = string.Join(
            " · ",
            new[] { m.AdapterName, string.IsNullOrWhiteSpace(m.Gateway) ? null : $"gateway {m.Gateway}" }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        return new DiagnosticStep(DiagnosticStepId.LocalNetwork, DiagnosticStepStatus.Ok, null, detail);
    }

    private static DiagnosticStep SystemDnsStep(NetworkMeasurements m)
    {
        var servers = m.SystemDnsServers.Count == 0 ? "system default" : string.Join(", ", m.SystemDnsServers);
        if (m.SystemDnsFailed)
        {
            return new DiagnosticStep(DiagnosticStepId.SystemDns, DiagnosticStepStatus.Failed, m.SystemDnsMs, $"{servers} · could not resolve the server name");
        }

        var slow = m.SystemDnsMs is { } ms && ms >= SlowThresholdMs;
        return new DiagnosticStep(
            DiagnosticStepId.SystemDns,
            slow ? DiagnosticStepStatus.Slow : DiagnosticStepStatus.Ok,
            m.SystemDnsMs,
            slow ? $"{servers} · answered, but slow" : servers);
    }

    private static DiagnosticStep PublicDnsStep(NetworkMeasurements m)
    {
        if (m.PublicDnsFailed)
        {
            return new DiagnosticStep(DiagnosticStepId.PublicDns, DiagnosticStepStatus.Failed, m.PublicDnsMs, "public DNS (1.1.1.1 / 8.8.8.8) did not answer");
        }

        if (m.PublicDnsMs is null)
        {
            return new DiagnosticStep(DiagnosticStepId.PublicDns, DiagnosticStepStatus.Skipped, null, "not checked");
        }

        var detail = m.SystemDnsFailed || m.SystemDnsAddresses.Count == 0
            ? string.Join(", ", m.PublicDnsAddresses)
            : AddressesDisjoint(m)
                ? "different address from system DNS"
                : "same address as system DNS";
        var slow = m.PublicDnsMs >= SlowThresholdMs;
        return new DiagnosticStep(
            DiagnosticStepId.PublicDns,
            slow ? DiagnosticStepStatus.Slow : DiagnosticStepStatus.Ok,
            m.PublicDnsMs,
            detail);
    }

    private static DiagnosticStep ConnectStep(
        DiagnosticStepId id,
        bool applicable,
        long? ms,
        bool failed,
        string notApplicableDetail)
    {
        if (!applicable)
        {
            return new DiagnosticStep(id, DiagnosticStepStatus.Skipped, null, notApplicableDetail);
        }

        if (failed)
        {
            return new DiagnosticStep(id, DiagnosticStepStatus.Failed, ms, "could not open a connection on port 443");
        }

        if (ms is null)
        {
            return new DiagnosticStep(id, DiagnosticStepStatus.Skipped, null, "not checked");
        }

        return new DiagnosticStep(
            id,
            ms >= SlowThresholdMs ? DiagnosticStepStatus.Slow : DiagnosticStepStatus.Ok,
            ms,
            "port 443");
    }

    private static DiagnosticStep ProxyStep(NetworkMeasurements m)
    {
        var description = m.ProxyConfigured && !string.IsNullOrWhiteSpace(m.ProxyAddress)
            ? $"proxy {m.ProxyAddress}"
            : m.ProxyAutoDetectEnabled
                ? "none set, automatic detection is on"
                : "none, connects directly";
        if (SystemProxyPathFailed(m))
        {
            return new DiagnosticStep(DiagnosticStepId.Proxy, DiagnosticStepStatus.Failed, m.SystemProxyReplyMs, $"{description} · works only without the proxy");
        }

        if (SystemProxyPathSlow(m))
        {
            return new DiagnosticStep(DiagnosticStepId.Proxy, DiagnosticStepStatus.Slow, m.SystemProxyReplyMs, $"{description} · slower than a direct connection");
        }

        return new DiagnosticStep(DiagnosticStepId.Proxy, DiagnosticStepStatus.Ok, null, description);
    }

    private static DiagnosticStep SecureReplyStep(NetworkMeasurements m)
    {
        return m.Reply switch
        {
            ReplyOutcome.Online => new DiagnosticStep(
                DiagnosticStepId.SecureReply,
                m.DirectReplyMs is { } ms && ms >= SlowThresholdMs ? DiagnosticStepStatus.Slow : DiagnosticStepStatus.Ok,
                m.DirectReplyMs,
                "HTTPS · server reports online"),
            ReplyOutcome.SecureConnectionFailed => new DiagnosticStep(DiagnosticStepId.SecureReply, DiagnosticStepStatus.Failed, m.DirectReplyMs, "secure connection could not be set up"),
            ReplyOutcome.ServerError => new DiagnosticStep(DiagnosticStepId.SecureReply, DiagnosticStepStatus.Failed, m.DirectReplyMs, $"server answered with HTTP {m.HttpStatus?.ToString() ?? "error"}"),
            ReplyOutcome.NotPosApi => new DiagnosticStep(DiagnosticStepId.SecureReply, DiagnosticStepStatus.Failed, m.DirectReplyMs, "the reply did not come from the POS server"),
            ReplyOutcome.Timeout => new DiagnosticStep(DiagnosticStepId.SecureReply, DiagnosticStepStatus.Failed, m.DirectReplyMs, "no reply in time"),
            ReplyOutcome.Unreachable => new DiagnosticStep(DiagnosticStepId.SecureReply, DiagnosticStepStatus.Failed, m.DirectReplyMs, "could not reach the server"),
            _ => new DiagnosticStep(DiagnosticStepId.SecureReply, DiagnosticStepStatus.Skipped, null, "not checked")
        };
    }

    private static DiagnosticStep ClockStep(NetworkMeasurements m)
    {
        if (m.ClockSkew is not { } skew)
        {
            return new DiagnosticStep(DiagnosticStepId.Clock, DiagnosticStepStatus.Skipped, null, "server time not available");
        }

        var seconds = (long)Math.Round(skew.Duration().TotalSeconds);
        return skew.Duration() > MaxClockSkew
            ? new DiagnosticStep(DiagnosticStepId.Clock, DiagnosticStepStatus.Failed, null, $"this machine is {FormatSkew(seconds)} off server time")
            : new DiagnosticStep(DiagnosticStepId.Clock, DiagnosticStepStatus.Ok, null, $"this machine is {seconds} s from server time");
    }

    private static string FormatSkew(long seconds)
    {
        return seconds >= 3600
            ? $"{seconds / 3600d:0.#} h"
            : seconds >= 120
                ? $"{seconds / 60d:0} min"
                : $"{seconds} s";
    }
}
