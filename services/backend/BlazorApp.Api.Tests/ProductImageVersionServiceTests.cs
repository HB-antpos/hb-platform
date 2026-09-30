using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductImageVersionServiceTests
{
    private const string ShanghaiHost = "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com";
    private const string SingaporeHost = "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetRecentVersionsAsync_只返回最近换过的白名单图片()
    {
        var recentlyReplaced = Now.AddHours(-1);
        var handler = new FakeCosHandler(new Dictionary<string, (HttpStatusCode, DateTimeOffset?)>
        {
            [$"{ShanghaiHost}/YW200/HB312-003.jpg"] = (HttpStatusCode.OK, recentlyReplaced),
            [$"{SingaporeHost}/250/OLD.jpg"] = (HttpStatusCode.OK, Now.AddDays(-45)),
            [$"{SingaporeHost}/250/MISSING.jpg"] = (HttpStatusCode.NotFound, null),
        });
        var service = CreateService(handler, new MemoryCache(new MemoryCacheOptions()));

        var versions = await service.GetRecentVersionsAsync(new[]
        {
            $"{ShanghaiHost}/YW200/HB312-003.jpg",
            $"{SingaporeHost}/250/OLD.jpg",
            $"{SingaporeHost}/250/MISSING.jpg",
            "https://img.supplier.example.com/a.jpg",
            "not a url",
            null,
        });

        var only = Assert.Single(versions);
        Assert.Equal($"{ShanghaiHost}/YW200/HB312-003.jpg", only.Key);
        Assert.Equal(recentlyReplaced.ToUnixTimeSeconds().ToString(), only.Value);
        // 非白名单地址绝不发请求，避免被当作任意地址探测器。
        Assert.DoesNotContain(handler.Requests.Keys, url => url.Contains("supplier.example.com"));
        Assert.All(handler.Requests.Keys, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public async Task GetRecentVersionsAsync_同一张图不同写法只探测一次且按原始地址回填()
    {
        var handler = new FakeCosHandler(new Dictionary<string, (HttpStatusCode, DateTimeOffset?)>
        {
            [$"{ShanghaiHost}/YW200/A.jpg"] = (HttpStatusCode.OK, Now.AddMinutes(-5)),
        });
        var service = CreateService(handler, new MemoryCache(new MemoryCacheOptions()));
        var httpSpelling = $"http://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/A.jpg";
        var paddedSpelling = $" {ShanghaiHost}/YW200/A.jpg?imageMogr2/thumbnail/72x72 ";

        var versions = await service.GetRecentVersionsAsync(new[] { httpSpelling, paddedSpelling });

        Assert.Equal(2, versions.Count);
        Assert.Equal(versions[httpSpelling], versions[paddedSpelling]);
        Assert.Equal(1, handler.Requests[$"{ShanghaiHost}/YW200/A.jpg"]);
    }

    [Fact]
    public async Task GetRecentVersionsAsync_成功结果进缓存_临时失败不进缓存()
    {
        var handler = new FakeCosHandler(new Dictionary<string, (HttpStatusCode, DateTimeOffset?)>
        {
            [$"{ShanghaiHost}/YW200/OK.jpg"] = (HttpStatusCode.OK, Now.AddMinutes(-5)),
            [$"{ShanghaiHost}/YW200/BUSY.jpg"] = (HttpStatusCode.ServiceUnavailable, null),
        });
        var service = CreateService(handler, new MemoryCache(new MemoryCacheOptions()));
        var urls = new[] { $"{ShanghaiHost}/YW200/OK.jpg", $"{ShanghaiHost}/YW200/BUSY.jpg" };

        await service.GetRecentVersionsAsync(urls);
        var second = await service.GetRecentVersionsAsync(urls);

        Assert.Single(second);
        Assert.Equal(1, handler.Requests[$"{ShanghaiHost}/YW200/OK.jpg"]);
        // 503 这类临时失败下次要重新探测，不能把“没版本号”缓存 10 分钟。
        Assert.Equal(2, handler.Requests[$"{ShanghaiHost}/YW200/BUSY.jpg"]);
    }

    [Fact]
    public async Task Controller_登录即可调用且限制单次数量()
    {
        Assert.NotNull(typeof(ReactProductImageVersionsController).GetCustomAttribute<AuthorizeAttribute>());

        var handler = new FakeCosHandler(new Dictionary<string, (HttpStatusCode, DateTimeOffset?)>());
        var controller = new ReactProductImageVersionsController(
            CreateService(handler, new MemoryCache(new MemoryCacheOptions())));
        var tooMany = Enumerable.Range(0, ProductImageVersionService.MaxUrlsPerRequest + 1)
            .Select(i => (string?)$"{ShanghaiHost}/YW200/{i}.jpg")
            .ToList();

        var result = await controller.GetVersions(new ProductImageVersionsRequest { Urls = tooMany });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(handler.Requests);

        var empty = Assert.IsType<OkObjectResult>(await controller.GetVersions(null));
        var payload = Assert.IsType<ApiResponse<ProductImageVersionsResult>>(empty.Value);
        Assert.Empty(payload.Data!.Versions);
    }

    private static ProductImageVersionService CreateService(FakeCosHandler handler, IMemoryCache cache) =>
        new(new HttpClient(handler), cache, new FixedTimeProvider(Now), NullLogger<ProductImageVersionService>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeCosHandler(IReadOnlyDictionary<string, (HttpStatusCode Status, DateTimeOffset? LastModified)> objects)
        : HttpMessageHandler
    {
        public ConcurrentDictionary<string, int> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            var url = request.RequestUri!.ToString();
            Requests.AddOrUpdate(url, 1, (_, count) => count + 1);

            var (status, lastModified) = objects.TryGetValue(url, out var found) ? found : (HttpStatusCode.NotFound, null);
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            response.Content.Headers.LastModified = lastModified;
            return Task.FromResult(response);
        }
    }
}
