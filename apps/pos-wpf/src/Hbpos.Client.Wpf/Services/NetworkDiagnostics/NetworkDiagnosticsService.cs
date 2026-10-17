using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

public interface INetworkDiagnosticsService
{
    Task<NetworkDiagnosisReport> RunAsync(Uri apiBaseAddress, CancellationToken cancellationToken = default);
}

/// <summary>
/// 只读检测：问系统 DNS、问公共 DNS、分别用 IPv4 / IPv6 连端口、直连与走系统代理各发一次健康检查，
/// 再交给 <see cref="NetworkDiagnosisRules"/> 得出原因。不修改任何设置。
/// </summary>
public sealed class NetworkDiagnosticsService(INetworkProbe probe, Func<DateTimeOffset>? clock = null) : INetworkDiagnosticsService
{
    /// <summary>参照地址：Cloudflare 的公共解析服务，443 端口对全球开放，用来对比"链路本身"和"到我们服务器的路径"。</summary>
    internal const string ReferenceAddress = "1.1.1.1";

    private const int GatewayPingCount = 6;
    private const int StabilityAttempts = 5;
    private const int BurstAttempts = 6;
    private static readonly TimeSpan StabilityAttemptTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StabilityBudget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan GatewayPingTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(12);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);

    public async Task<NetworkDiagnosisReport> RunAsync(Uri apiBaseAddress, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var started = _now();
        var host = apiBaseAddress.Host;
        var port = apiBaseAddress.Port;
        var healthUri = new Uri(apiBaseAddress, "api/v1/health");
        var isLiteral = IPAddress.TryParse(host, out _) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

        var local = probe.GetLocalNetwork();
        var proxy = probe.GetProxyInfo(apiBaseAddress);

        // 网关 ping 与 DNS 查询同时进行；最长约 6 秒，不会拉长总耗时。
        Task<PingProbeResult>? gatewayTask = local.HasNetwork && !string.IsNullOrWhiteSpace(local.Gateway)
            ? probe.PingAsync(local.Gateway, GatewayPingCount, GatewayPingTimeout, cancellationToken)
            : null;

        DnsProbeResult? systemDns = null;
        DnsProbeResult? publicDns = null;
        if (!isLiteral)
        {
            var systemTask = probe.ResolveSystemDnsAsync(host, StepTimeout, cancellationToken);
            var publicTask = probe.ResolvePublicDnsAsync(host, StepTimeout, cancellationToken);
            await Task.WhenAll(systemTask, publicTask);
            systemDns = await systemTask;
            publicDns = await publicTask;
        }

        // 连接测试优先用系统 DNS 的答案（用户机器实际会连的地址）；系统 DNS 失败再用公共 DNS 的答案。
        var addresses = (systemDns is { Failed: false } ? systemDns.Addresses : publicDns?.Addresses) ?? [host];
        if (isLiteral)
        {
            // localhost 不是地址：按本机 IPv4 回环测试连接。
            addresses = [string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : host];
        }

        var ipv4 = addresses.FirstOrDefault(address => address.Contains('.'));
        var ipv6 = addresses.FirstOrDefault(address => address.Contains(':'));
        ConnectProbeResult? ipv4Result = null;
        ConnectProbeResult? ipv6Result = null;
        var connectTasks = new List<Task>();
        Task<ConnectProbeResult>? ipv4Task = ipv4 is null ? null : probe.ConnectAsync(ipv4, port, StepTimeout, cancellationToken);
        Task<ConnectProbeResult>? ipv6Task = ipv6 is null ? null : probe.ConnectAsync(ipv6, port, StepTimeout, cancellationToken);
        if (ipv4Task is not null)
        {
            connectTasks.Add(ipv4Task);
        }

        if (ipv6Task is not null)
        {
            connectTasks.Add(ipv6Task);
        }

        // 两种健康检查同时发，省总时间；各自带独立的新连接。
        var directTask = probe.CheckReplyAsync(healthUri, useSystemProxy: false, ReplyTimeout, cancellationToken);
        var viaSystemTask = probe.CheckReplyAsync(healthUri, useSystemProxy: true, ReplyTimeout, cancellationToken);
        // 参照连接：本机开发地址（localhost / IP 字面量）不做，没有可比性。
        Task<ConnectProbeResult>? referenceTask = isLiteral
            ? null
            : probe.ConnectAsync(ReferenceAddress, 443, StepTimeout, cancellationToken);
        if (referenceTask is not null)
        {
            connectTasks.Add(referenceTask);
        }

        if (gatewayTask is not null)
        {
            connectTasks.Add(gatewayTask);
        }

        // TLS：与 HTTP 检查同时进行。"不联网校验"那一次只为读取证书链并给出基准耗时；
        // "默认校验"那一次与 HttpClient 完全一致，卡在联网查证书上的话会在这里超时。只对 https 且非 IP / localhost 做。
        var probeTls = !isLiteral && string.Equals(apiBaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        Task<TlsProbeResult>? tlsOfflineTask = probeTls ? probe.ProbeTlsAsync(host, port, true, StepTimeout, cancellationToken) : null;
        Task<TlsProbeResult>? tlsDefaultTask = probeTls ? probe.ProbeTlsAsync(host, port, false, ReplyTimeout, cancellationToken) : null;
        if (tlsOfflineTask is not null)
        {
            connectTasks.Add(tlsOfflineTask);
        }

        if (tlsDefaultTask is not null)
        {
            connectTasks.Add(tlsDefaultTask);
        }

        connectTasks.Add(directTask);
        connectTasks.Add(viaSystemTask);
        await Task.WhenAll(connectTasks);
        ipv4Result = ipv4Task is null ? null : await ipv4Task;
        ipv6Result = ipv6Task is null ? null : await ipv6Task;
        // 连接稳定性放在主检测之后单独测：逐个连（看是否偶发丢握手包）、同时连（看是否被限制并发新连接）。
        // 不和其他探测同时进行，否则我们自己的并发会让路由器限制连接，反过来污染结果。
        ConnectStabilityResult? serverSequential = null;
        ConnectStabilityResult? referenceSequential = null;
        ConnectStabilityResult? serverBurst = null;
        if (!isLiteral && ipv4 is not null)
        {
            var serverSequentialTask = probe.ProbeConnectStabilityAsync(ipv4, port, StabilityAttempts, false, StabilityAttemptTimeout, StabilityBudget, cancellationToken);
            var referenceSequentialTask = probe.ProbeConnectStabilityAsync(ReferenceAddress, 443, StabilityAttempts, false, StabilityAttemptTimeout, StabilityBudget, cancellationToken);
            await Task.WhenAll(serverSequentialTask, referenceSequentialTask);
            serverSequential = await serverSequentialTask;
            referenceSequential = await referenceSequentialTask;
            serverBurst = await probe.ProbeConnectStabilityAsync(ipv4, port, BurstAttempts, true, StabilityAttemptTimeout, StabilityBudget, cancellationToken);
        }

        var tlsOffline = tlsOfflineTask is null ? null : await tlsOfflineTask;
        var tlsDefault = tlsDefaultTask is null ? null : await tlsDefaultTask;
        // 证书链信息优先用"不联网校验"那次（不会被联网查证书卡住）；它没拿到时退而用默认那次。
        var chain = tlsOffline is { ChainChecked: true } ? tlsOffline : tlsDefault is { ChainChecked: true } ? tlsDefault : null;
        var gateway = gatewayTask is null ? null : await gatewayTask;
        var reference = referenceTask is null ? null : await referenceTask;
        var direct = await directTask;
        var viaSystem = await viaSystemTask;

        TimeSpan? skew = null;
        if (direct.ServerTime is { } serverTime)
        {
            // 用请求耗时的一半作为单程延迟的粗略校正。
            skew = started.AddMilliseconds(direct.ElapsedMs / 2d) - serverTime;
        }

        var measurements = new NetworkMeasurements
        {
            HasLocalNetwork = local.HasNetwork,
            AdapterName = local.AdapterName,
            Gateway = local.Gateway,
            SystemDnsServers = local.DnsServers,
            SystemDnsMs = systemDns?.ElapsedMs,
            SystemDnsAddresses = systemDns?.Addresses ?? [],
            SystemDnsFailed = systemDns?.Failed ?? false,
            PublicDnsMs = publicDns?.ElapsedMs,
            PublicDnsAddresses = publicDns?.Addresses ?? [],
            PublicDnsFailed = publicDns?.Failed ?? false,
            Ipv4ConnectMs = ipv4Result?.ElapsedMs,
            Ipv4ConnectFailed = ipv4Result?.Failed ?? (ipv4 is null && !isLiteral && systemDns is { Failed: true } && publicDns is { Failed: true }),
            TlsOutcome = tlsDefault?.Outcome ?? TlsProbeOutcome.Unknown,
            TlsHandshakeMs = tlsDefault?.HandshakeMs,
            TlsProtocol = tlsDefault?.Protocol ?? tlsOffline?.Protocol,
            TlsError = tlsDefault?.Error,
            TlsOfflineHandshakeMs = tlsOffline is { Outcome: TlsProbeOutcome.Completed } ? tlsOffline.HandshakeMs : null,
            ChainChecked = chain is not null,
            ChainTrusted = chain?.ChainTrusted ?? false,
            ChainNotTimeValid = chain?.NotTimeValid ?? false,
            ChainProblems = chain?.ChainProblems ?? [],
            ChainSubjects = chain?.ChainSubjects ?? [],
            CertificateIssuer = chain?.Issuer,
            MissingTrustedRoots = chain is { ChainTrusted: false } ? probe.GetMissingTrustedRoots() : [],
            StabilityAttempts = serverSequential?.Attempts,
            StabilitySucceeded = serverSequential?.Succeeded,
            StabilityAvgMs = serverSequential?.AvgMs,
            StabilityMaxMs = serverSequential?.MaxMs,
            ReferenceStabilityAttempts = referenceSequential?.Attempts,
            ReferenceStabilitySucceeded = referenceSequential?.Succeeded,
            ReferenceStabilityMaxMs = referenceSequential?.MaxMs,
            BurstAttempts = serverBurst?.Attempts,
            BurstSucceeded = serverBurst?.Succeeded,
            BurstMaxMs = serverBurst?.MaxMs,
            GatewayPingSent = gateway?.Sent,
            GatewayPingLost = gateway?.Lost,
            GatewayAvgMs = gateway?.AvgMs,
            GatewayMaxMs = gateway?.MaxMs,
            ReferenceHost = reference is null ? null : ReferenceAddress,
            ReferenceConnectMs = reference?.ElapsedMs,
            ReferenceConnectFailed = reference?.Failed ?? false,
            HasIpv6Address = ipv6 is not null,
            Ipv6ConnectMs = ipv6Result?.ElapsedMs,
            Ipv6ConnectFailed = ipv6Result?.Failed ?? false,
            ProxyConfigured = proxy.Configured,
            ProxyAddress = proxy.Address,
            ProxyAutoDetectEnabled = proxy.AutoDetectEnabled,
            DirectReplyMs = direct.ElapsedMs,
            DirectReplyFailed = direct.Outcome != ReplyOutcome.Online,
            SystemProxyReplyMs = viaSystem.ElapsedMs,
            SystemProxyReplyFailed = viaSystem.Outcome != ReplyOutcome.Online,
            Reply = direct.Outcome,
            HttpStatus = direct.HttpStatus,
            ClockSkew = skew
        };

        var steps = NetworkDiagnosisRules.BuildSteps(measurements);
        var finding = NetworkDiagnosisRules.Evaluate(measurements);
        return new NetworkDiagnosisReport(
            apiBaseAddress.GetLeftPart(UriPartial.Path),
            started,
            stopwatch.Elapsed,
            steps,
            finding,
            measurements,
            $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}");
    }
}
