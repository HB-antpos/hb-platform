using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/image-proxy")]
    [Authorize]
    public class ReactImageProxyController : ControllerBase
    {
        private const int MaxImageBytes = 8 * 1024 * 1024;
        private static readonly TimeSpan ProxyTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DnsLookupTimeout = TimeSpan.FromSeconds(3);
        private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com",
            "hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com"
        };
        private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
            "image/gif",
            "image/avif"
        };

        private static readonly HttpClient ProxyClient = new(
            new SocketsHttpHandler
            {
                // 禁止自动跳转，避免白名单域名 302 到内网/metadata 地址形成 SSRF。
                AllowAutoRedirect = false
            }
        )
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly HttpClient _proxyClient;
        private readonly Func<string, CancellationToken, Task<bool>> _isSafePublicHostAsync;

        public ReactImageProxyController(IHttpClientFactory httpClientFactory)
            : this(ProxyClient, IsSafePublicHostAsync)
        {
            _ = httpClientFactory;
        }

        private ReactImageProxyController(
            HttpClient proxyClient,
            Func<string, CancellationToken, Task<bool>> isSafePublicHostAsync
        )
        {
            _proxyClient = proxyClient;
            _isSafePublicHostAsync = isSafePublicHostAsync;
        }

        [HttpGet]
        [Authorize(Roles = "Admin,WarehouseManager,WarehouseStaff")]
        public async Task<IActionResult> Get([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return BadRequest("url is required");

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return BadRequest("invalid url");

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return BadRequest("unsupported scheme");

            if (!IsAllowedImageHost(uri))
                return BadRequest("unsupported image host");

            if (!await _isSafePublicHostAsync(uri.Host, HttpContext.RequestAborted))
                return BadRequest("unsafe image host");

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36"
            );
            req.Headers.Referrer = new Uri($"{uri.Scheme}://{uri.Host}/");
            req.Headers.TryAddWithoutValidation(
                "Accept",
                "image/avif,image/webp,image/apng,image/*,*/*;q=0.8"
            );

            try
            {
                // 浏览器取消导出时立即停止上游下载；代理自身仍有独立的超时上限。
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                timeoutCts.CancelAfter(ProxyTimeout);
                using var resp = await _proxyClient.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token
                );

                if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400)
                    return BadRequest("redirect is not allowed");

                if (!resp.IsSuccessStatusCode)
                    return StatusCode((int)resp.StatusCode);

                var contentType =
                    resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                if (!AllowedImageContentTypes.Contains(contentType))
                    return NotFound("not image");

                if (resp.Content.Headers.ContentLength > MaxImageBytes)
                    return BadRequest("image too large");

                var bytes = await ReadLimitedAsync(resp.Content, MaxImageBytes, timeoutCts.Token);
                if (!HasValidImageSignature(bytes, contentType))
                    return NotFound("not image");

                // 浏览器必须按服务端校验后的 MIME 处理，避免 SVG/HTML 等主动内容被嗅探执行。
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(bytes, contentType);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                return new EmptyResult();
            }
            catch (OperationCanceledException)
            {
                return StatusCode(504, "image proxy timeout");
            }
            catch (HttpRequestException)
            {
                return BadRequest("image request failed");
            }
            catch (InvalidOperationException ex) when (ex.Message == "image too large")
            {
                return BadRequest("image too large");
            }
        }

        // 销售明细仅需读取相同白名单和图片校验下的商品缩略图。
        [HttpGet("sales-detail")]
        [Authorize(Policy = Permissions.SalesDashboard.SalesDetailView)]
        public Task<IActionResult> GetSalesDetail([FromQuery] string url) => Get(url);

        // 独立销售看板导出商品明细带图：看板用户未必有销售明细权限，按看板权限单独放行，白名单与图片校验相同。
        [HttpGet("compact-sales-board")]
        [Authorize(Policy = Permissions.SalesDashboard.CompactBoardView)]
        public Task<IActionResult> GetCompactSalesBoard([FromQuery] string url) => Get(url);

        private static bool IsAllowedImageHost(Uri uri)
        {
            return AllowedImageHosts.Contains(uri.IdnHost);
        }

        private static async Task<bool> IsSafePublicHostAsync(string host, CancellationToken cancellationToken)
        {
            try
            {
                IPAddress[] addresses;
                if (IPAddress.TryParse(host, out var literalAddress))
                {
                    addresses = new[] { literalAddress };
                }
                else
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(DnsLookupTimeout);
                    addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);
                }

                return addresses.Length > 0 && addresses.Select(NormalizeAddress).All(IsPublicAddress);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        private static bool IsPublicAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return false;

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var ipv6Bytes = address.GetAddressBytes();

                // SSRF 防护：拒绝本机、未指定、链路本地、站点本地、ULA、组播和文档 IPv6 段。
                return !address.Equals(IPAddress.IPv6None)
                    && !address.Equals(IPAddress.IPv6Any)
                    && !address.IsIPv6LinkLocal
                    && !address.IsIPv6SiteLocal
                    && !address.IsIPv6Multicast
                    && !IsIPv6UniqueLocal(ipv6Bytes)
                    && !IsIPv6Documentation(ipv6Bytes);
            }

            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
                return false;

            // SSRF 防护：拒绝内网、链路本地、metadata、CGNAT、文档/保留/组播等非公网 IPv4。
            return bytes[0] != 10
                && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                && !(bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] != 127
                && bytes[0] != 0
                && bytes[0] < 224
                && bytes[0] < 240;
        }

        private static bool IsIPv6UniqueLocal(byte[] bytes)
        {
            return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
        }

        private static bool IsIPv6Documentation(byte[] bytes)
        {
            return bytes.Length == 16 && bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8;
        }

        private static bool HasValidImageSignature(byte[] bytes, string contentType)
        {
            // MIME 和魔数必须同时匹配，拒绝伪装成图片的 SVG/HTML/脚本内容。
            return contentType.ToLowerInvariant() switch
            {
                "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
                "image/png" => bytes.Length >= 8
                    && bytes[0] == 0x89
                    && bytes[1] == 0x50
                    && bytes[2] == 0x4e
                    && bytes[3] == 0x47
                    && bytes[4] == 0x0d
                    && bytes[5] == 0x0a
                    && bytes[6] == 0x1a
                    && bytes[7] == 0x0a,
                "image/webp" => bytes.Length >= 12
                    && bytes[0] == 0x52
                    && bytes[1] == 0x49
                    && bytes[2] == 0x46
                    && bytes[3] == 0x46
                    && bytes[8] == 0x57
                    && bytes[9] == 0x45
                    && bytes[10] == 0x42
                    && bytes[11] == 0x50,
                "image/gif" => bytes.Length >= 6
                    && bytes[0] == 0x47
                    && bytes[1] == 0x49
                    && bytes[2] == 0x46
                    && bytes[3] == 0x38
                    && (bytes[4] == 0x37 || bytes[4] == 0x39)
                    && bytes[5] == 0x61,
                "image/avif" => bytes.Length >= 12
                    && bytes[4] == 0x66
                    && bytes[5] == 0x74
                    && bytes[6] == 0x79
                    && bytes[7] == 0x70
                    && bytes[8] == 0x61
                    && bytes[9] == 0x76
                    && bytes[10] == 0x69
                    && (bytes[11] == 0x66 || bytes[11] == 0x73),
                _ => false
            };
        }

        private static async Task<byte[]> ReadLimitedAsync(
            HttpContent content,
            int maxBytes,
            CancellationToken cancellationToken
        )
        {
            await using var source = await content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new InvalidOperationException("image too large");

                // 流式累加并逐块检查大小，避免异常图片一次性撑爆内存。
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            return buffer.ToArray();
        }
    }
}
