using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

/// <summary>
/// 经完整中间件管线（UseResponseCompression + 控制器）验证目录响应压缩白名单：
/// 码冲突候选在客户端声明 gzip 时压缩，未声明时（旧版 WPF）与启用前一样返回明文；
/// v1 分页与其他端点即使声明 gzip 也保持未压缩。
/// </summary>
public sealed class CatalogResponseCompressionHttpTests
{
    private const string CodeConflictsUri = "/api/v1/catalog/sellable-items/code-conflicts?storeCode=S01";
    // 中文注释：与 SocketsHttpHandler 开启 GZip | Deflate | Brotli 后自动发送的请求头一致（新版 WPF 目录客户端）。
    private const string NewWpfAcceptEncoding = "gzip, deflate, br";
    private const int ItemCount = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Code_conflicts_is_gzip_compressed_when_client_accepts_gzip()
    {
        await using var factory = new CatalogCompressionApiFactory();
        using var client = CreateDeviceClient(factory);

        using var compressed = await GetAsync(client, CodeConflictsUri, NewWpfAcceptEncoding);
        using var plain = await GetAsync(client, CodeConflictsUri, acceptEncoding: null);

        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        Assert.Equal("gzip", Assert.Single(compressed.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", compressed.Headers.Vary);
        var compressedBytes = await compressed.Content.ReadAsByteArrayAsync();
        var plainBytes = await plain.Content.ReadAsByteArrayAsync();
        Assert.True(
            compressedBytes.Length < plainBytes.Length / 4,
            $"gzip 后 {compressedBytes.Length} 字节，明文 {plainBytes.Length} 字节");
        // 中文注释：解压后与未压缩响应逐字节一致，客户端拿到的 JSON 不变。
        var decompressed = Gunzip(compressedBytes);
        Assert.Equal(plainBytes, decompressed);
        Assert.Equal(ItemCount, ReadData<CatalogCodeConflictsResponse>(decompressed).Items.Count);
    }

    [Fact]
    public async Task Code_conflicts_is_not_compressed_without_accept_encoding()
    {
        await using var factory = new CatalogCompressionApiFactory();
        using var client = CreateDeviceClient(factory);

        using var response = await GetAsync(client, CodeConflictsUri, acceptEncoding: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNotCompressed(response);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(ItemCount, ReadData<CatalogCodeConflictsResponse>(body).Items.Count);
    }

    [Theory]
    [InlineData("&checksumVersion=1")]
    [InlineData("")]
    public async Task V1_catalog_pages_are_not_compressed_even_when_client_accepts_gzip(string checksumQuery)
    {
        await using var factory = new CatalogCompressionApiFactory();
        using var client = CreateDeviceClient(factory);

        using var response = await GetAsync(
            client,
            $"/api/v1/catalog/sellable-items/page?storeCode=S01&pageSize=5000{checksumQuery}",
            NewWpfAcceptEncoding);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNotCompressed(response);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(ItemCount, ReadData<CatalogSyncPageResponse>(body).Items.Count);
    }

    [Fact]
    public async Task Catalog_v2_pages_are_still_compressed_when_client_accepts_gzip()
    {
        await using var factory = new CatalogCompressionApiFactory();
        using var client = CreateDeviceClient(factory);

        using var response = await GetAsync(
            client,
            "/api/v1/catalog/sellable-items/page?storeCode=S01&pageSize=5000&checksumVersion=2",
            NewWpfAcceptEncoding);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        var body = Gunzip(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(ItemCount, ReadData<CatalogSyncPageResponse>(body).Items.Count);
    }

    [Theory]
    [InlineData("/api/v1/catalog/promotions?storeCode=S01")]
    [InlineData("/api/v1/catalog/sellable-items?storeCode=S01")]
    [InlineData("/api/v1/catalog/sellable-items/lookup?storeCode=S01&lookupCode=6900000000001")]
    [InlineData("/api/v1/health")]
    public async Task Other_endpoints_are_not_compressed_even_when_client_accepts_gzip(string requestUri)
    {
        await using var factory = new CatalogCompressionApiFactory();
        using var client = CreateDeviceClient(factory);

        using var response = await GetAsync(client, requestUri, NewWpfAcceptEncoding);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNotCompressed(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
    }

    private static HttpClient CreateDeviceClient(CatalogCompressionApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "device");
        return client;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string requestUri, string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        return await client.SendAsync(request);
    }

    private static void AssertNotCompressed(HttpResponseMessage response)
    {
        Assert.Empty(response.Content.Headers.ContentEncoding);
        // 中文注释：未进入白名单时压缩中间件也不追加 Vary，响应头与启用压缩前一致。
        Assert.DoesNotContain("Accept-Encoding", response.Headers.Vary);
    }

    private static byte[] Gunzip(byte[] compressed)
    {
        using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static T ReadData<T>(byte[] body)
    {
        var result = JsonSerializer.Deserialize<ApiResult<T>>(body, JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        return result.Data;
    }

    private sealed class CatalogCompressionApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                    options.DefaultChallengeScheme = TestAuthHandler.Scheme;
                    options.DefaultScheme = TestAuthHandler.Scheme;
                });
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
                services.RemoveAll<ICatalogService>();
                services.AddSingleton<ICatalogService>(new CompressionCatalogService());
                var noOp = new NoOpSchemaInitializer();
                services.RemoveAll<IStoreSchemaInitializer>();
                services.AddSingleton<IStoreSchemaInitializer>(noOp);
                services.RemoveAll<IAttendanceQrKeySchemaInitializer>();
                services.AddSingleton<IAttendanceQrKeySchemaInitializer>(noOp);
                services.RemoveAll<IAdvertisementSchemaInitializer>();
                services.AddSingleton<IAdvertisementSchemaInitializer>(noOp);
                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton<ILinklyCloudCredentialSchemaInitializer>(noOp);
                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton<ISquareTokenSchemaInitializer>(noOp);
            });
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public new const string Scheme = "CatalogCompressionHttpTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(
                [
                    new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01"),
                    new Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
                    new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
                ], Scheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }

    private sealed class NoOpSchemaInitializer :
        IStoreSchemaInitializer,
        IAttendanceQrKeySchemaInitializer,
        IAdvertisementSchemaInitializer,
        ILinklyCloudCredentialSchemaInitializer,
        ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// 只实现本测试会命中的目录读取接口；条目数足够大，压缩与否一眼可辨。
    /// </summary>
    private sealed class CompressionCatalogService : ICatalogService
    {
        private static readonly DateTimeOffset GeneratedAt = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

        private static readonly IReadOnlyList<CatalogLookupItemDto> Items = Enumerable.Range(1, ItemCount)
            .Select(index => new CatalogLookupItemDto(
                "S01",
                $"P{index:D5}",
                null,
                $"测试商品 {index}",
                "6900000000001",
                "6900000000001",
                $"IT{index:D5}",
                "6900000000001",
                9.99m,
                PriceSourceKind.StoreRetailPrice,
                "门店零售价",
                1m,
                GeneratedAt,
                null))
            .ToArray();

        public Task<CatalogCodeConflictsResponse?> GetCodeConflictsAsync(string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogCodeConflictsResponse?>(new(storeCode, GeneratedAt, true, Items));

        public Task<CatalogSyncPageResponse?> GetSellableItemsPageWithLeaseAsync(
            string storeCode, DateTimeOffset? since, string? cursor, int pageSize,
            CancellationToken cancellationToken, string? catalogVersion, int checksumVersion, string? downloadLeaseId) =>
            Task.FromResult<CatalogSyncPageResponse?>(new(storeCode, GeneratedAt, cursor, Items, [], null, false, Items.Count));

        public Task<CatalogPromotionsResponse?> GetPromotionRulesAsync(string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogPromotionsResponse?>(new(storeCode, GeneratedAt, []));

        public Task<SellableItemsResponse?> GetSellableItemsAsync(string storeCode, DateTimeOffset? since, CancellationToken cancellationToken) =>
            Task.FromResult<SellableItemsResponse?>(new(storeCode, GeneratedAt, []));

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(
            string storeCode, string? lookupCode, string? lookupCodeNormalized, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogLookupResponse?>(new(storeCode, lookupCode ?? string.Empty, Items[0].LookupCodeNormalized, true, Items[0]));

        public Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(string storeCode, DateTimeOffset? since, string? cursor, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(string storeCode, DateTimeOffset? since, string? cursor, int pageSize, CancellationToken cancellationToken, string? catalogVersion, int checksumVersion) => throw new NotSupportedException();
        public Task<CatalogSyncPlanResponse?> GetCatalogSyncPlanAsync(string storeCode, string? baseCatalogVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogSyncPlanResponse?> GetCatalogSyncPlanWithLeaseAsync(string storeCode, string? baseCatalogVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogDeltaPageResponse> GetCatalogDeltaPageAsync(string storeCode, string baseCatalogVersion, string targetCatalogVersion, string? cursor, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogDeltaPageResponse> GetCatalogDeltaPageWithLeaseAsync(string storeCode, string baseCatalogVersion, string targetCatalogVersion, string? cursor, int pageSize, CancellationToken cancellationToken, string? downloadLeaseId) => throw new NotSupportedException();
        public Task<CatalogCompareResponse?> CompareSellableItemsAsync(CatalogCompareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogSpecialProductsPageResponse?> GetSpecialProductsPageAsync(string storeCode, string? cursor, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogSpecialProductMarkServiceResult> MarkSpecialProductAsync(CatalogSpecialProductMarkRequest request, string updatedBy, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
