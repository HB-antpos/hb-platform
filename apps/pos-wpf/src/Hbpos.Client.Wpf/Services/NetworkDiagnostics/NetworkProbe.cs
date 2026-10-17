using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Health;
using Microsoft.Win32;

namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

public sealed record LocalNetworkInfo(
    bool HasNetwork,
    string? AdapterName,
    string? Gateway,
    IReadOnlyList<string> DnsServers,
    int? AdapterIndex = null);

public sealed record DnsProbeResult(long ElapsedMs, IReadOnlyList<string> Addresses, bool Failed);

public sealed record ConnectProbeResult(long ElapsedMs, bool Failed);

/// <summary>对网关发 ping 的统计：发了几次、丢了几次、成功的平均 / 最大往返毫秒。</summary>
public sealed record PingProbeResult(int Sent, int Lost, long? AvgMs, long? MaxMs);

public sealed record ProxyProbeInfo(bool Configured, string? Address, bool AutoDetectEnabled);

public sealed record ReplyProbeResult(
    long ElapsedMs,
    ReplyOutcome Outcome,
    int? HttpStatus,
    DateTimeOffset? ServerTime);

/// <summary>网络检测用到的全部"外部动作"。服务只依赖这个接口，测试里换成假实现。</summary>
public interface INetworkProbe
{
    LocalNetworkInfo GetLocalNetwork();

    Task<DnsProbeResult> ResolveSystemDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken);

    Task<DnsProbeResult> ResolvePublicDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken);

    Task<ConnectProbeResult> ConnectAsync(string address, int port, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>连续 ping 指定地址（通常是网关）count 次，统计丢包与延迟。</summary>
    Task<PingProbeResult> PingAsync(string address, int count, TimeSpan timeoutPerPing, CancellationToken cancellationToken);

    ProxyProbeInfo GetProxyInfo(Uri target);

    Task<ReplyProbeResult> CheckReplyAsync(Uri healthUri, bool useSystemProxy, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SystemNetworkProbe : INetworkProbe
{
    internal static readonly IPAddress[] PublicDnsServers =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8")
    ];

    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public LocalNetworkInfo GetLocalNetwork()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .FirstOrDefault(address => !IPAddress.Any.Equals(address) && !IPAddress.IPv6Any.Equals(address));
                if (gateway is null)
                {
                    continue;
                }

                int? index = null;
                try
                {
                    index = properties.GetIPv4Properties()?.Index;
                }
                catch (NetworkInformationException)
                {
                    // 没有 IPv4 属性的网卡不能用编号改 DNS，保持 null。
                }

                return new LocalNetworkInfo(
                    true,
                    adapter.Name,
                    gateway.ToString(),
                    properties.DnsAddresses.Select(address => address.ToString()).ToArray(),
                    index);
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            // 读取失败时不当成"没网"，按未知处理由后续探测决定。
            return new LocalNetworkInfo(true, null, null, []);
        }

        return new LocalNetworkInfo(false, null, null, []);
    }

    public async Task<DnsProbeResult> ResolveSystemDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).WaitAsync(timeout, cancellationToken);
            return new DnsProbeResult(
                stopwatch.ElapsedMilliseconds,
                addresses.Select(address => address.ToString()).ToArray(),
                addresses.Length == 0);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new DnsProbeResult(stopwatch.ElapsedMilliseconds, [], true);
        }
    }

    public async Task<DnsProbeResult> ResolvePublicDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (var server in PublicDnsServers)
        {
            var addresses = await DnsWireClient.QueryAsync(server, host, timeout, cancellationToken);
            if (addresses is { Count: > 0 })
            {
                return new DnsProbeResult(stopwatch.ElapsedMilliseconds, addresses, false);
            }
        }

        return new DnsProbeResult(stopwatch.ElapsedMilliseconds, [], true);
    }

    public async Task<ConnectProbeResult> ConnectAsync(string address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!IPAddress.TryParse(address, out var ip))
        {
            return new ConnectProbeResult(0, true);
        }

        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limited.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), limited.Token);
            return new ConnectProbeResult(stopwatch.ElapsedMilliseconds, false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ConnectProbeResult(stopwatch.ElapsedMilliseconds, true);
        }
    }

    public async Task<PingProbeResult> PingAsync(string address, int count, TimeSpan timeoutPerPing, CancellationToken cancellationToken)
    {
        var lost = 0;
        var replies = new List<long>();
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(address, (int)timeoutPerPing.TotalMilliseconds);
                if (reply.Status == IPStatus.Success)
                {
                    replies.Add(reply.RoundtripTime);
                }
                else
                {
                    lost++;
                }
            }
            catch (Exception ex) when (ex is PingException or InvalidOperationException or ArgumentException)
            {
                // 发不出去（没有路由 / 网卡刚掉线）也算这一次丢了。
                lost++;
            }
        }

        return new PingProbeResult(
            count,
            lost,
            replies.Count == 0 ? null : (long)Math.Round(replies.Average()),
            replies.Count == 0 ? null : replies.Max());
    }

    public ProxyProbeInfo GetProxyInfo(Uri target)
    {
        var autoDetect = false;
        string? configured = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            // 值缺省（从未设置）时 Windows 视为开启自动检测。
            autoDetect = key?.GetValue("AutoDetect") is not int detect || detect != 0;
            if (key?.GetValue("ProxyEnable") is int enabled && enabled == 1
                && key.GetValue("ProxyServer") is string server
                && !string.IsNullOrWhiteSpace(server))
            {
                configured = server;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 读不到注册表不影响检测，按"未配置"处理。
        }

        return new ProxyProbeInfo(configured is not null, configured, autoDetect);
    }

    public async Task<ReplyProbeResult> CheckReplyAsync(Uri healthUri, bool useSystemProxy, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = useSystemProxy,
            // 每次检测都要走完整的 DNS + 握手，不复用连接。
            PooledConnectionLifetime = TimeSpan.Zero
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limited.CancelAfter(timeout);
        try
        {
            using var response = await client.GetAsync(healthUri, limited.Token);
            var status = (int)response.StatusCode;
            if (status >= 500 || !response.IsSuccessStatusCode)
            {
                return new ReplyProbeResult(stopwatch.ElapsedMilliseconds, ReplyOutcome.ServerError, status, null);
            }

            var content = await response.Content.ReadAsStringAsync(limited.Token);
            try
            {
                using var document = JsonDocument.Parse(content);
                if (!DeviceApiClient.IsApiResultEnvelope(document.RootElement))
                {
                    return new ReplyProbeResult(stopwatch.ElapsedMilliseconds, ReplyOutcome.NotPosApi, status, null);
                }

                var result = document.RootElement.Deserialize<ApiResult<HealthCheckResponse>>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return result?.Success == true && result.Data?.IsOnline == true
                    ? new ReplyProbeResult(stopwatch.ElapsedMilliseconds, ReplyOutcome.Online, status, result.Data.ServerTime)
                    : new ReplyProbeResult(stopwatch.ElapsedMilliseconds, ReplyOutcome.ServerError, status, null);
            }
            catch (JsonException)
            {
                return new ReplyProbeResult(stopwatch.ElapsedMilliseconds, ReplyOutcome.NotPosApi, status, null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or AuthenticationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ReplyProbeResult(stopwatch.ElapsedMilliseconds, Classify(ex), null, null);
        }
    }

    internal static ReplyOutcome Classify(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return ReplyOutcome.SecureConnectionFailed;
            }

            if (current is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
            {
                return ReplyOutcome.SecureConnectionFailed;
            }
        }

        return exception is OperationCanceledException || exception.InnerException is TimeoutException
            ? ReplyOutcome.Timeout
            : ReplyOutcome.Unreachable;
    }
}
