namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

/// <summary>
/// 把实测数据变成"逐项状态 + 结论"。纯函数，不碰网络和系统设置，阈值与判断全部在这里，方便单元测试。
/// 客户端对设备 API 的请求只给几秒，所以"慢"按 1.5 秒算：超过它就会表现为 Offline。
/// </summary>
public static class NetworkDiagnosisRules
{
    public const long SlowThresholdMs = 1500;
    public const long FastThresholdMs = 500;

    /// <summary>到路由器丢包达到这个比例（%）就认为本机链路不稳。</summary>
    public const int GatewayLossPercentThreshold = 10;

    /// <summary>
    /// TLS 握手本来就要 2 到 3 个往返（服务器在海外时往返约 250 ms），再叠加并发检测和首次加载，健康机器上也会到 2 秒多，
    /// 所以握手不能和 DNS / 连接共用 1.5 秒的"慢"阈值；绝对值超过它才算慢，或者比不联网校验慢 1.5 秒以上（慢在联网查证书）。
    /// </summary>
    public const long TlsSlowThresholdMs = 4000;

    /// <summary>到路由器的平均延迟超过它也认为链路不稳（同一个路由器的正常值通常只有几毫秒）。</summary>
    public const long GatewaySlowAvgMs = 300;

    /// <summary>
    /// 服务器证书预期的签发者关键字。收到的证书签发者不含它们，说明有软件在中间重签（杀毒的 HTTPS 扫描、代理、防火墙设备）。
    /// 将来服务器换了证书颁发机构时，这里要同步更新；结论里会把实际签发者名字显示出来，所以误报也容易看出来。
    /// </summary>
    public static readonly string[] ExpectedIssuerMarkers = ["Let's Encrypt", "ISRG"];

    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<DiagnosticStep> BuildSteps(NetworkMeasurements m)
    {
        return
        [
            LocalNetworkStep(m),
            GatewayStep(m),
            SystemDnsStep(m),
            PublicDnsStep(m),
            ConnectStep(DiagnosticStepId.ConnectIpv4, true, m.Ipv4ConnectMs, m.Ipv4ConnectFailed, "no IPv4 address returned"),
            ConnectStep(DiagnosticStepId.ConnectIpv6, m.HasIpv6Address, m.Ipv6ConnectMs, m.Ipv6ConnectFailed, "no IPv6 address returned"),
            ReferenceStep(m),
            ConnectStabilityStep(m),
            ConnectBurstStep(m),
            ProxyStep(m),
            TlsHandshakeStep(m),
            CertificateTrustStep(m),
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

        if (GatewayUnstable(m))
        {
            return new DiagnosisFinding(DiagnosisKind.LocalLinkUnstable, null);
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

        if (m.ChainChecked && m.ChainNotTimeValid)
        {
            return new DiagnosisFinding(DiagnosisKind.ClockSkew, null);
        }

        if (m.ChainChecked && IssuerUnexpected(m) && (m.Reply != ReplyOutcome.Online || TlsSlowOrFailed(m)))
        {
            return new DiagnosisFinding(DiagnosisKind.HttpsIntercepted, null);
        }

        if (m.ChainChecked && !m.ChainTrusted && m.Reply != ReplyOutcome.Online)
        {
            return m.MissingTrustedRoots.Count > 0
                ? new DiagnosisFinding(DiagnosisKind.CertificateRootMissing, NetworkFixKind.InstallTrustedRoots)
                : new DiagnosisFinding(DiagnosisKind.SecureConnectionFailed, null);
        }

        if (m.ChainChecked && m.ChainTrusted && CertificateLookupSlow(m))
        {
            return new DiagnosisFinding(DiagnosisKind.CertificateCheckSlow, null);
        }

        if (StabilityMeasured(m) && StabilityClean(m.ReferenceStabilityAttempts, m.ReferenceStabilitySucceeded, m.ReferenceStabilityMaxMs))
        {
            var sequentialClean = StabilityClean(m.StabilityAttempts, m.StabilitySucceeded, m.StabilityMaxMs);

            // 逐个连接偶发失败或要好几秒，参照却每次正常：到服务器的路径在丢握手包。一个都连不上的不算（那是被挡，走下面的规则）。
            if (!sequentialClean && m.StabilitySucceeded > 0)
            {
                return new DiagnosisFinding(DiagnosisKind.ServerPathLossy, null);
            }

            // 逐个都正常，同时连多个才失败（或主检测里并发的那次连接失败了）：有东西在限制并发新连接。
            if (sequentialClean
                && (m.Ipv4ConnectFailed || BurstMeasured(m) && !StabilityClean(m.BurstAttempts, m.BurstSucceeded, m.BurstMaxMs)))
            {
                return new DiagnosisFinding(DiagnosisKind.ConnectionBurstLimited, null);
            }
        }

        if (m.Reply is ReplyOutcome.Timeout or ReplyOutcome.Unreachable
            && (m.Ipv4ConnectFailed || m.Ipv4ConnectMs is { } serverConnect && serverConnect >= SlowThresholdMs)
            && ReferenceMeasured(m))
        {
            if (m.ReferenceConnectFailed || m.ReferenceConnectMs is { } reference && reference >= SlowThresholdMs)
            {
                return new DiagnosisFinding(DiagnosisKind.InternetUnreachable, null);
            }

            if (m.ReferenceConnectMs is { } referenceMs && referenceMs < FastThresholdMs)
            {
                return new DiagnosisFinding(DiagnosisKind.ServerPathBlocked, null);
            }
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

    private static bool GatewayMeasured(NetworkMeasurements m) => m.GatewayPingSent is > 0 && m.GatewayPingLost is not null;

    private static bool GatewayAllLost(NetworkMeasurements m) => GatewayMeasured(m) && m.GatewayPingLost >= m.GatewayPingSent;

    private static int GatewayLossPercent(NetworkMeasurements m) =>
        GatewayMeasured(m) ? (int)Math.Round(100d * m.GatewayPingLost!.Value / m.GatewayPingSent!.Value) : 0;

    /// <summary>
    /// 全部丢失不算"不稳"：很多路由器不回 ping，这种情况只能说没有结论，不能据此怪链路。
    /// </summary>
    private static bool GatewayUnstable(NetworkMeasurements m)
    {
        return GatewayMeasured(m)
            && !GatewayAllLost(m)
            && (GatewayLossPercent(m) >= GatewayLossPercentThreshold || m.GatewayAvgMs is { } avg && avg >= GatewaySlowAvgMs);
    }

    private static bool TlsSlowOrFailed(NetworkMeasurements m)
    {
        return m.TlsOutcome is TlsProbeOutcome.TimedOut or TlsProbeOutcome.Failed
            || m.TlsHandshakeMs is { } ms && ms >= TlsSlowThresholdMs
            || m.TlsHandshakeMs is { } total && m.TlsOfflineHandshakeMs is { } offline && total - offline >= SlowThresholdMs;
    }

    /// <summary>
    /// 不联网校验时握手很快，而默认方式（会联网查根证书 / 吊销信息）超时或慢了 1.5 秒以上：慢在 Windows 联网查证书上。
    /// </summary>
    private static bool CertificateLookupSlow(NetworkMeasurements m)
    {
        if (m.TlsOfflineHandshakeMs is not { } offline)
        {
            return false;
        }

        return m.TlsOutcome == TlsProbeOutcome.TimedOut
            || m.TlsOutcome == TlsProbeOutcome.Completed && m.TlsHandshakeMs is { } handshake && handshake - offline >= SlowThresholdMs;
    }

    private static bool IssuerUnexpected(NetworkMeasurements m)
    {
        return !string.IsNullOrWhiteSpace(m.CertificateIssuer)
            && !ExpectedIssuerMarkers.Any(marker => m.CertificateIssuer.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把 "CN=YE2, O=Let's Encrypt, C=US" 这类主题缩成人能读的名字。</summary>
    internal static string ShortName(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "unknown";
        }

        string? Part(string key) => subject.Split(',')
            .Select(item => item.Trim())
            .FirstOrDefault(item => item.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..];

        var cn = Part("CN");
        var org = Part("O");
        return cn is not null && org is not null && !cn.Contains(org, StringComparison.OrdinalIgnoreCase)
            ? $"{org} {cn}"
            : cn ?? org ?? subject;
    }

    private static bool StabilityMeasured(NetworkMeasurements m) => m.StabilityAttempts is > 0;

    private static bool BurstMeasured(NetworkMeasurements m) => m.BurstAttempts is > 0;

    /// <summary>这一组连接全部成功，而且最慢的一次也不到 1.5 秒（没有触发握手包重传）。</summary>
    private static bool StabilityClean(int? attempts, int? succeeded, long? maxMs)
    {
        return attempts is > 0 && succeeded == attempts && (maxMs ?? 0) < SlowThresholdMs;
    }

    private static DiagnosticStep StabilityRow(
        DiagnosticStepId id,
        int? attempts,
        int? succeeded,
        long? avgMs,
        long? maxMs,
        string prefix,
        string? suffix)
    {
        if (attempts is not > 0)
        {
            return new DiagnosticStep(id, DiagnosticStepStatus.Skipped, null, "not checked");
        }

        var parts = new List<string> { $"{prefix}{succeeded ?? 0}/{attempts} connected" };
        if (avgMs is { } avg)
        {
            parts.Add($"avg {NetworkDiagnosisReport.FormatMs(avg)}");
        }

        if (maxMs is { } max)
        {
            parts.Add($"max {NetworkDiagnosisReport.FormatMs(max)}");
        }

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            parts.Add(suffix);
        }

        var status = succeeded < attempts
            ? DiagnosticStepStatus.Failed
            : maxMs is { } slowest && slowest >= SlowThresholdMs ? DiagnosticStepStatus.Slow : DiagnosticStepStatus.Ok;
        return new DiagnosticStep(id, status, avgMs, string.Join(" · ", parts));
    }

    private static DiagnosticStep ConnectStabilityStep(NetworkMeasurements m)
    {
        var reference = m.ReferenceStabilityAttempts is > 0
            ? $"reference {m.ReferenceStabilitySucceeded ?? 0}/{m.ReferenceStabilityAttempts}"
            : null;
        return StabilityRow(
            DiagnosticStepId.ConnectStability,
            m.StabilityAttempts,
            m.StabilitySucceeded,
            m.StabilityAvgMs,
            m.StabilityMaxMs,
            "one at a time: ",
            reference);
    }

    private static DiagnosticStep ConnectBurstStep(NetworkMeasurements m)
    {
        return StabilityRow(
            DiagnosticStepId.ConnectBurst,
            m.BurstAttempts,
            m.BurstSucceeded,
            null,
            m.BurstMaxMs,
            $"{m.BurstAttempts} at once: ",
            null);
    }

    private static bool ReferenceMeasured(NetworkMeasurements m) => m.ReferenceConnectFailed || m.ReferenceConnectMs is not null;

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

    private static DiagnosticStep GatewayStep(NetworkMeasurements m)
    {
        if (!GatewayMeasured(m))
        {
            return new DiagnosticStep(DiagnosticStepId.GatewayLink, DiagnosticStepStatus.Skipped, null, "not checked");
        }

        if (GatewayAllLost(m))
        {
            return new DiagnosticStep(DiagnosticStepId.GatewayLink, DiagnosticStepStatus.Skipped, null, "the router did not answer ping (it may block ping)");
        }

        var loss = GatewayLossPercent(m);
        var detail = string.Join(
            " · ",
            new[]
            {
                m.Gateway,
                $"{loss}% lost",
                m.GatewayAvgMs is { } avg ? $"avg {NetworkDiagnosisReport.FormatMs(avg)}" : null,
                m.GatewayMaxMs is { } max ? $"max {NetworkDiagnosisReport.FormatMs(max)}" : null
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var status = loss >= 30
            ? DiagnosticStepStatus.Failed
            : GatewayUnstable(m) ? DiagnosticStepStatus.Slow : DiagnosticStepStatus.Ok;
        return new DiagnosticStep(DiagnosticStepId.GatewayLink, status, m.GatewayAvgMs, detail);
    }

    private static DiagnosticStep ReferenceStep(NetworkMeasurements m)
    {
        if (!ReferenceMeasured(m))
        {
            return new DiagnosticStep(DiagnosticStepId.ReferenceConnect, DiagnosticStepStatus.Skipped, null, "not checked");
        }

        var host = string.IsNullOrWhiteSpace(m.ReferenceHost) ? "reference host" : m.ReferenceHost;
        if (m.ReferenceConnectFailed)
        {
            return new DiagnosticStep(DiagnosticStepId.ReferenceConnect, DiagnosticStepStatus.Failed, m.ReferenceConnectMs, $"{host} port 443 · a different server, used for comparison · could not connect");
        }

        var slow = m.ReferenceConnectMs is { } ms && ms >= SlowThresholdMs;
        return new DiagnosticStep(
            DiagnosticStepId.ReferenceConnect,
            slow ? DiagnosticStepStatus.Slow : DiagnosticStepStatus.Ok,
            m.ReferenceConnectMs,
            $"{host} port 443 · a different server, used for comparison");
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

    private static DiagnosticStep TlsHandshakeStep(NetworkMeasurements m)
    {
        switch (m.TlsOutcome)
        {
            case TlsProbeOutcome.Completed:
            {
                var detail = string.IsNullOrWhiteSpace(m.TlsProtocol) ? "secure connection set up" : m.TlsProtocol!;
                if (m.TlsOfflineHandshakeMs is { } offline && m.TlsHandshakeMs is { } total && total - offline >= SlowThresholdMs)
                {
                    detail += $" · {NetworkDiagnosisReport.FormatMs(total - offline)} of it spent checking the certificate online";
                }

                var slow = m.TlsHandshakeMs is { } ms && ms >= TlsSlowThresholdMs
                    || m.TlsHandshakeMs is { } handshake && m.TlsOfflineHandshakeMs is { } offlineMs && handshake - offlineMs >= SlowThresholdMs;
                return new DiagnosticStep(
                    DiagnosticStepId.TlsHandshake,
                    slow ? DiagnosticStepStatus.Slow : DiagnosticStepStatus.Ok,
                    m.TlsHandshakeMs,
                    detail);
            }

            case TlsProbeOutcome.TimedOut:
                return new DiagnosticStep(DiagnosticStepId.TlsHandshake, DiagnosticStepStatus.Failed, m.TlsHandshakeMs, "the secure handshake did not finish in time");
            case TlsProbeOutcome.Failed:
                return new DiagnosticStep(DiagnosticStepId.TlsHandshake, DiagnosticStepStatus.Failed, m.TlsHandshakeMs, string.IsNullOrWhiteSpace(m.TlsError) ? "the secure handshake failed" : m.TlsError!);
            default:
                return new DiagnosticStep(DiagnosticStepId.TlsHandshake, DiagnosticStepStatus.Skipped, null, "not checked");
        }
    }

    private static DiagnosticStep CertificateTrustStep(NetworkMeasurements m)
    {
        if (!m.ChainChecked)
        {
            return new DiagnosticStep(DiagnosticStepId.CertificateTrust, DiagnosticStepStatus.Skipped, null, "could not read the server certificate");
        }

        var issuer = ShortName(m.CertificateIssuer);
        if (m.ChainNotTimeValid)
        {
            return new DiagnosticStep(DiagnosticStepId.CertificateTrust, DiagnosticStepStatus.Failed, null, "the certificate dates do not match this machine's clock");
        }

        if (IssuerUnexpected(m))
        {
            return new DiagnosticStep(
                DiagnosticStepId.CertificateTrust,
                DiagnosticStepStatus.Failed,
                null,
                $"issued by {issuer}, not by Let's Encrypt · something on this machine or network is re-signing HTTPS");
        }

        if (m.ChainTrusted)
        {
            return new DiagnosticStep(DiagnosticStepId.CertificateTrust, DiagnosticStepStatus.Ok, null, $"{m.ChainSubjects.Count} certificates · issued by {issuer}");
        }

        var why = m.MissingTrustedRoots.Count > 0
            ? $"this machine does not trust the root: {string.Join(", ", m.MissingTrustedRoots)}"
            : m.ChainProblems.Count > 0 ? $"problem: {string.Join(", ", m.ChainProblems)}" : "the certificate chain is not trusted";
        return new DiagnosticStep(DiagnosticStepId.CertificateTrust, DiagnosticStepStatus.Failed, null, $"{why} · issued by {issuer}");
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
