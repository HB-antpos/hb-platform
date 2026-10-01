using System.Globalization;
using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace BlazorApp.Api.Services;

/// <summary>
/// 商品图片版本号：向 COS 读取图片的 Last-Modified，供前端给缩略图地址追加版本参数。
///
/// 背景：COS 图片处理（imageMogr2 缩略图）的响应带 Cache-Control: max-age=2592000（30 天强缓存），
/// 而商品图常在 COS 上按原文件名直接覆盖，不经过本系统、数据库也不变。
/// 浏览器会在 30 天内继续显示旧缩略图，只有用 COS 自身的修改时间才能知道图片换过。
/// </summary>
public sealed class ProductImageVersionService
{
    /// <summary>单次请求最多查询的图片数，覆盖列表页一页的图片量。</summary>
    public const int MaxUrlsPerRequest = 200;

    /// <summary>
    /// 只给这段时间内改过的图片返回版本号。
    /// 更早修改的图片，改动之前缓存的旧缩略图已超过 30 天强缓存而失效，不需要换地址；
    /// 多留 1 天余量，覆盖浏览器与服务器的时钟偏差。
    /// </summary>
    public static readonly TimeSpan RecentChangeWindow = TimeSpan.FromDays(31);

    /// <summary>探测结果缓存时长，决定换图后最迟多久前端能拿到新版本号。</summary>
    public static readonly TimeSpan ProbeCacheDuration = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    // 与图片代理同一份白名单：只探测自家公开读 COS 桶，避免被当成任意地址探测器（SSRF）。
    private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com",
        "hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com",
    };

    // 全进程共享的并发上限：多个用户同时翻页时，发往 COS 的 HEAD 请求总数也受控。
    private static readonly SemaphoreSlim ProbeConcurrency = new(16);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProductImageVersionService> _logger;

    public ProductImageVersionService(
        HttpClient httpClient,
        IMemoryCache cache,
        TimeProvider timeProvider,
        ILogger<ProductImageVersionService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// 返回「原始地址 → 版本号」：只包含白名单 COS 桶内、最近 <see cref="RecentChangeWindow"/> 内改过的图片。
    /// 版本号是 Last-Modified 的 Unix 秒数；查询失败、不存在或很久没改过的图片不出现在结果里，前端按原地址显示。
    /// </summary>
    public async Task<Dictionary<string, string>> GetRecentVersionsAsync(IEnumerable<string?> urls)
    {
        // 同一张图可能以不同写法（首尾空格、带查询串）出现，按规范化地址去重后只探测一次。
        var requested = new List<(string Original, string Normalized)>();
        foreach (var url in urls)
        {
            if (url is null || !TryNormalizeImageUrl(url, out var normalized))
            {
                continue;
            }
            requested.Add((url, normalized));
        }

        var probes = requested
            .Select(item => item.Normalized)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(normalized => normalized, GetLastModifiedAsync, StringComparer.Ordinal);
        await Task.WhenAll(probes.Values);

        var cutoff = _timeProvider.GetUtcNow() - RecentChangeWindow;
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (original, normalized) in requested)
        {
            var lastModified = probes[normalized].Result;
            if (lastModified is { } value && value >= cutoff)
            {
                versions[original] = value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            }
        }
        return versions;
    }

    /// <summary>只接受白名单 COS 桶的 http(s) 地址；去掉查询串与片段，得到对象本身的地址。</summary>
    internal static bool TryNormalizeImageUrl(string url, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !AllowedImageHosts.Contains(uri.Host))
        {
            return false;
        }

        // 统一走 https：两个桶都支持，且与 http 写法共享同一条缓存。
        normalized = "https://" + uri.Host.ToLowerInvariant() + uri.AbsolutePath;
        return uri.AbsolutePath.Length > 1;
    }

    private async Task<DateTimeOffset?> GetLastModifiedAsync(string normalizedUrl)
    {
        var cacheKey = "product-image-version:" + normalizedUrl;
        // 缓存的是 Task：同一张图的并发请求共享同一次 HEAD，不会重复打 COS。
        var probe = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ProbeCacheDuration;
            return ProbeAsync(normalizedUrl);
        })!;

        var result = await probe.ConfigureAwait(false);
        if (!result.Succeeded
            && _cache.TryGetValue(cacheKey, out Task<ProbeResult>? cached)
            && ReferenceEquals(cached, probe))
        {
            // 超时、网络错误等临时失败不缓存，下次翻页重新探测；404 属于确定结果，照常缓存。
            // 只移除自己这次的失败结果，不误删别的请求已经写入的新结果。
            _cache.Remove(cacheKey);
        }
        return result.LastModified;
    }

    private readonly record struct ProbeResult(bool Succeeded, DateTimeOffset? LastModified);

    private async Task<ProbeResult> ProbeAsync(string normalizedUrl)
    {
        // 探测结果被多个请求共享，不能绑定某个请求的取消令牌，只用自己的超时。
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        var entered = false;
        try
        {
            await ProbeConcurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;

            using var request = new HttpRequestMessage(HttpMethod.Head, normalizedUrl);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ProbeResult(true, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(false, null);
            }
            return new ProbeResult(true, response.Content.Headers.LastModified);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "商品图片版本探测失败：{Url}", normalizedUrl);
            return new ProbeResult(false, null);
        }
        finally
        {
            if (entered)
            {
                ProbeConcurrency.Release();
            }
        }
    }
}
