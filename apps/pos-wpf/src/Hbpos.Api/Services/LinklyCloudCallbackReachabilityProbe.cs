using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hbpos.Api.Logging;

namespace Hbpos.Api.Services;

/// <summary>
/// 一次回调可达性自探测的结果。Reason 是给运维看的短码，不含 URL 查询串、bearer 或响应正文。
/// </summary>
public sealed record LinklyCloudCallbackProbeResult(bool Reachable, string Reason, int? HttpStatus);

/// <summary>
/// 端到端验证 Linkly 回调链路：POS 自己带着正确 bearer，经公网地址（Nginx /pos-api/）向 cloud-notifications 发一次 POST。
/// 回调被 WAF、IP 白名单或路由前缀改写拦截时，设备 API 仍然正常、健康检查仍然绿色，
/// 但按键提示（display）和回单（receipt）会静默丢失，只有轮询还能拿到交易结果——这类问题很难定位。
/// </summary>
public interface ILinklyCloudCallbackReachabilityProbe
{
    Task<LinklyCloudCallbackProbeResult> ProbeAsync(
        string environment,
        Uri publicNotificationBaseUri,
        string bearer,
        CancellationToken cancellationToken);
}

public sealed class HttpLinklyCloudCallbackReachabilityProbe(
    HttpClient httpClient,
    TimeProvider? timeProvider = null,
    ILogger<HttpLinklyCloudCallbackReachabilityProbe>? logger = null) : ILinklyCloudCallbackReachabilityProbe
{
    // 成功结果缓存较久；失败缓存较短，修复 Nginx 后很快能恢复绿色。健康检查被每台 POS 频繁调用，不能每次都打一次公网请求。
    internal static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public Task<LinklyCloudCallbackProbeResult> ProbeAsync(
        string environment,
        Uri publicNotificationBaseUri,
        string bearer,
        CancellationToken cancellationToken)
    {
        var key = BuildCacheKey(environment, publicNotificationBaseUri, bearer);
        var now = clock.GetUtcNow();
        CacheEntry entry;
        lock (gate)
        {
            if (!cache.TryGetValue(key, out entry!) || entry.ExpiresAt <= now)
            {
                // 探测本身不绑定某个调用方的取消令牌：结果会共享给并发调用方，
                // 不能因为一个请求被取消就让所有人拿到取消结果，也不能让缓存条目永远停在“进行中”。
                entry = new CacheEntry();
                entry.Task = RunAsync(entry, environment, publicNotificationBaseUri, bearer);
                cache[key] = entry;
            }
        }

        return entry.Task!.WaitAsync(cancellationToken);
    }

    private async Task<LinklyCloudCallbackProbeResult> RunAsync(
        CacheEntry entry,
        string environment,
        Uri publicNotificationBaseUri,
        string bearer)
    {
        // 让出线程，保证调用方先把 entry.Task 赋好再进入下面的逻辑。
        await Task.Yield();
        var result = await SendProbeAsync(environment, publicNotificationBaseUri, bearer);
        lock (gate)
        {
            entry.ExpiresAt = clock.GetUtcNow() + (result.Reachable ? SuccessCacheDuration : FailureCacheDuration);
        }

        return result;
    }

    private async Task<LinklyCloudCallbackProbeResult> SendProbeAsync(
        string environment,
        Uri publicNotificationBaseUri,
        string bearer)
    {
        // 随机 sessionId：ReceiveNotificationAsync 对不存在的会话鉴权后直接忽略、不写库，探针不会污染任何真实会话。
        var probeUri = new Uri(
            publicNotificationBaseUri,
            $"api/v1/linkly/cloud-notifications/{Uri.EscapeDataString(environment)}/{Guid.NewGuid():D}/display");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, probeUri)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var status = (int)response.StatusCode;
            var result = response.StatusCode switch
            {
                >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => new LinklyCloudCallbackProbeResult(true, "ok", status),
                HttpStatusCode.Unauthorized => new(false, "unauthorized-through-public-url", status),
                HttpStatusCode.Forbidden => new(false, "forbidden-by-proxy-or-waf", status),
                HttpStatusCode.NotFound => new(false, "route-not-found-check-proxy-prefix", status),
                HttpStatusCode.TooManyRequests => new(false, "rate-limited", status),
                _ => new(false, $"http-{status}", status)
            };
            LogResult(environment, publicNotificationBaseUri, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return Failed(environment, publicNotificationBaseUri, "timeout");
        }
        catch (HttpRequestException ex)
        {
            // 只保留异常类型：DNS / TLS / 连接被拒，具体原因在 InnerException，但不把 URL 之外的内容写进结果。
            return Failed(environment, publicNotificationBaseUri, $"unreachable-{ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            return Failed(environment, publicNotificationBaseUri, $"error-{ex.GetType().Name}");
        }
    }

    private LinklyCloudCallbackProbeResult Failed(string environment, Uri baseUri, string reason)
    {
        var result = new LinklyCloudCallbackProbeResult(false, reason, null);
        LogResult(environment, baseUri, result);
        return result;
    }

    private void LogResult(string environment, Uri baseUri, LinklyCloudCallbackProbeResult result)
    {
        if (result.Reachable || logger is null)
        {
            return;
        }

        // 失败走 Warning 才会进入中心日志；缓存已把频率限制在 30 秒一次，这里再按环境限频到 5 分钟，防止长时间故障刷屏。
        if (LogThrottle.Shared.TryAcquire($"linkly-callback-probe:{environment}", TimeSpan.FromMinutes(5), out var suppressed))
        {
            logger.LogWarning(
                new EventId(0, $"linkly-callback-unreachable:{result.Reason}"),
                "Linkly Cloud callback self-probe failed environment={Environment} host={Host} reason={Reason} httpStatus={HttpStatus} suppressed={Suppressed}. "
                + "Display prompts and receipts from Linkly may be lost; transaction results are still collected by polling. Check the reverse proxy route for /pos-api/.",
                environment,
                baseUri.Host,
                result.Reason,
                result.HttpStatus,
                suppressed);
        }
    }

    private static string BuildCacheKey(string environment, Uri baseUri, string bearer)
    {
        // 缓存键包含 bearer 的哈希而不是原文：bearer 换了（运维修复配置）要立即重新探测。
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer)))[..16];
        return $"{environment}|{baseUri.AbsoluteUri}|{digest}";
    }

    private sealed class CacheEntry
    {
        public Task<LinklyCloudCallbackProbeResult>? Task { get; set; }

        // 进行中的探测不过期（后来的调用方共享同一个任务）；完成后改为真实的缓存到期时间。
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MaxValue;
    }
}
