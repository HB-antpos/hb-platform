using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BlazorApp.Api.Features.ProductDescriptions;

/// <summary>
/// 下载商品图片并压成适合发给模型的 JPEG。
/// 图片地址来自前端（COS、hbimgoss 或供应商外链），因此在「建立连接」时校验目标 IP，
/// 每次跳转都会重新走 ConnectCallback，DNS 重绑定也绕不过去。
/// </summary>
public sealed class ProductImageFetcher : IProductImageFetcher
{
    public const string HttpClientName = "product-description-images";

    // 解码前先读尺寸，拒绝像素数异常的图片，防止解压炸弹撑爆内存。
    private const long MaxDecodedPixels = 50_000_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProductImageDescriptionOptions _options;

    public ProductImageFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<ProductImageDescriptionOptions> options
    )
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    /// <summary>供 Program.cs 注册命名 HttpClient：只连公网地址，最多跟随 3 次跳转。</summary>
    public static SocketsHttpHandler CreatePrimaryHandler() =>
        new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = ConnectToPublicAddressAsync,
        };

    public async Task<ProductImagePayload> FetchAsync(string imageUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(imageUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ProductImageFetchException("图片地址无效");
        }

        var bytes = await DownloadAsync(uri, cancellationToken);
        return EncodeForModel(bytes);
    }

    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ImageTimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // 部分供应商站点按 UA / Referer 防盗链，沿用图片代理的请求头。
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36"
        );
        request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
        request.Headers.Referrer = new Uri($"{uri.Scheme}://{uri.Host}/");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token
            );
            if (!response.IsSuccessStatusCode)
            {
                throw new ProductImageFetchException($"图片下载失败（HTTP {(int)response.StatusCode}）");
            }

            if (response.Content.Headers.ContentLength > _options.MaxImageBytes)
            {
                throw new ProductImageFetchException("图片过大");
            }

            return await ReadLimitedAsync(response.Content, _options.MaxImageBytes, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProductImageFetchException("图片下载超时");
        }
        catch (HttpRequestException ex)
        {
            throw new ProductImageFetchException("图片下载失败", ex);
        }
    }

    private ProductImagePayload EncodeForModel(byte[] bytes)
    {
        try
        {
            var info = Image.Identify(bytes);
            if (info == null)
            {
                throw new ProductImageFetchException("不是可识别的图片");
            }

            if ((long)info.Width * info.Height > MaxDecodedPixels)
            {
                throw new ProductImageFetchException("图片尺寸过大");
            }

            using var image = Image.Load(bytes);
            var edge = Math.Max(1, _options.MaxImageEdge);
            image.Mutate(context =>
            {
                context.AutoOrient();
                if (image.Width > edge || image.Height > edge)
                {
                    context.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(edge, edge) });
                }

                // 统一转 JPEG：透明底先铺白，避免透明区域变黑影响模型识别。
                context.BackgroundColor(Color.White);
            });

            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = 85 });
            return new ProductImagePayload(output.ToArray(), "image/jpeg");
        }
        catch (ProductImageFetchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw new ProductImageFetchException("不是可识别的图片", ex);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)) > 0)
        {
            // 没有 Content-Length 的响应也要逐块限长，避免异常大文件撑爆内存。
            if (buffer.Length + read > maxBytes)
            {
                throw new ProductImageFetchException("图片过大");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        var normalized = addresses.Select(NormalizeAddress).ToArray();
        // 任一解析结果是内网地址就整体拒绝，不挑一个公网地址连，避免混合解析被利用。
        if (normalized.Length == 0 || !normalized.All(IsPublicAddress))
        {
            throw new HttpRequestException("图片地址指向非公网地址");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(normalized, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>与 ReactImageProxyController 相同的公网判定：拒绝本机、内网、链路本地、metadata、CGNAT、保留与组播段。</summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var v6 = address.GetAddressBytes();
            return !address.Equals(IPAddress.IPv6None)
                && !address.Equals(IPAddress.IPv6Any)
                && !address.IsIPv6LinkLocal
                && !address.IsIPv6SiteLocal
                && !address.IsIPv6Multicast
                && !(v6.Length == 16 && (v6[0] & 0xfe) == 0xfc)
                && !(v6.Length == 16 && v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8);
        }

        var b = address.GetAddressBytes();
        if (b.Length != 4)
        {
            return false;
        }

        return b[0] != 10
            && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            && !(b[0] == 192 && b[1] == 168)
            && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            && !(b[0] == 192 && b[1] == 0 && b[2] == 0)
            && !(b[0] == 192 && b[1] == 0 && b[2] == 2)
            && !(b[0] == 198 && (b[1] == 18 || b[1] == 19))
            && !(b[0] == 198 && b[1] == 51 && b[2] == 100)
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113)
            && b[0] != 127
            && b[0] != 0
            && b[0] < 224;
    }
}
