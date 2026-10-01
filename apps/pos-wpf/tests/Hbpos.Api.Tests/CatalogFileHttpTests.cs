using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
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
/// 经完整中间件管线验证整文件下载：开关、设备门店范围、Range 断点续传、If-Range 失配回整份，
/// 以及响应压缩中间件不会对已经是 gzip 的文件再压一次（否则客户端算出的 SHA-256 对不上）。
/// </summary>
public sealed class CatalogFileHttpTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hbpos-catalog-files-http-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Endpoints_return_disabled_when_switch_is_off()
    {
        await using var factory = new CatalogFileApiFactory(_root, enabled: false);
        using var client = CreateDeviceClient(factory);

        using var plan = await client.GetAsync("/api/v1/catalog/files/sync-plan?storeCode=S01");
        using var full = await client.GetAsync("/api/v1/catalog/files/full?storeCode=S01&catalogVersion=v1");

        Assert.Equal(HttpStatusCode.NotFound, plan.StatusCode);
        Assert.Equal("CATALOG_FILE_DOWNLOAD_DISABLED", ReadError(await plan.Content.ReadAsByteArrayAsync()));
        Assert.Equal(HttpStatusCode.NotFound, full.StatusCode);
    }

    [Fact]
    public async Task Full_file_downloads_whole_and_resumes_with_range()
    {
        await using var factory = new CatalogFileApiFactory(_root, enabled: true);
        using var client = CreateDeviceClient(factory);
        var file = await GetPlannedFileAsync(client);

        using var whole = await SendAsync(client, file.Path, request =>
            request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br"));
        var wholeBytes = await whole.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Empty(whole.Content.Headers.ContentEncoding);
        Assert.Equal(CatalogFileFormats.ContentType, whole.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", Assert.Single(whole.Headers.AcceptRanges));
        Assert.Equal(file.Bytes, wholeBytes.Length);
        Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(wholeBytes)).ToLowerInvariant());

        // 断点续传：带 If-Range（ETag）只取剩余部分，拼接后与整份逐字节一致。
        var offset = wholeBytes.Length / 3;
        using var rest = await SendAsync(client, file.Path, request =>
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(whole.Headers.ETag!);
        });
        Assert.Equal(HttpStatusCode.PartialContent, rest.StatusCode);
        var restBytes = await rest.Content.ReadAsByteArrayAsync();
        Assert.Equal(wholeBytes[offset..], restBytes);

        // 本地残片属于另一个版本时 If-Range 失配，服务端回整份 200，客户端据此丢弃残片。
        using var mismatch = await SendAsync(client, file.Path, request =>
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"other\""));
        });
        Assert.Equal(HttpStatusCode.OK, mismatch.StatusCode);
        Assert.Equal(wholeBytes.Length, (await mismatch.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Other_store_device_cannot_read_plan_or_file()
    {
        await using var factory = new CatalogFileApiFactory(_root, enabled: true);
        using var client = CreateDeviceClient(factory);
        var file = await GetPlannedFileAsync(client);

        using var plan = await client.GetAsync("/api/v1/catalog/files/sync-plan?storeCode=S02");
        using var full = await client.GetAsync(file.Path.Replace("storeCode=S01", "storeCode=S02", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, plan.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, full.StatusCode);
    }

    [Fact]
    public async Task Unknown_version_returns_not_found_so_client_requests_new_plan()
    {
        await using var factory = new CatalogFileApiFactory(_root, enabled: true);
        using var client = CreateDeviceClient(factory);

        using var response = await client.GetAsync("/api/v1/catalog/files/full?storeCode=S01&catalogVersion=v-gone");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("CATALOG_FILE_NOT_FOUND", ReadError(await response.Content.ReadAsByteArrayAsync()));
    }

    private static async Task<CatalogDownloadFileDto> GetPlannedFileAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/catalog/files/sync-plan?storeCode=S01");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<ApiResult<CatalogFileSyncPlanResponse>>(
            await response.Content.ReadAsByteArrayAsync(),
            JsonOptions)!;
        Assert.Equal(CatalogSyncModes.Full, result.Data!.Mode);
        return result.Data.File!;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string relativePath,
        Action<HttpRequestMessage> configure)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/" + relativePath);
        configure(request);
        return await client.SendAsync(request);
    }

    private static string? ReadError(byte[] body) =>
        JsonSerializer.Deserialize<ApiResult<object>>(body, JsonOptions)?.ErrorCode;

    private static HttpClient CreateDeviceClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "device-token");
        return client;
    }

    private sealed class CatalogFileApiFactory(string root, bool enabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("CatalogDownloadFiles:Enabled", enabled ? "true" : "false");
            builder.UseSetting("CatalogDownloadFiles:RootPath", root);
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                    options.DefaultChallengeScheme = TestAuthHandler.Scheme;
                    options.DefaultScheme = TestAuthHandler.Scheme;
                });
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
                services.RemoveAll<ICatalogTargetIndexSource>();
                // 足够多的条目让 gzip 文件跨越多个 Range 分段。
                services.AddSingleton<ICatalogTargetIndexSource>(new CatalogDownloadFileStoreTests.FakeTargetIndexSource(
                    CatalogDownloadFileStoreTests.BuildResult(
                        "v1",
                        Enumerable.Range(1, 3000).Select(index => ($"93{index:D10}", 1m + index)))));
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
        public new const string Scheme = "CatalogFileHttpTest";

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
}
