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
        connectTasks.Add(directTask);
        connectTasks.Add(viaSystemTask);
        await Task.WhenAll(connectTasks);
        ipv4Result = ipv4Task is null ? null : await ipv4Task;
        ipv6Result = ipv6Task is null ? null : await ipv6Task;
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
