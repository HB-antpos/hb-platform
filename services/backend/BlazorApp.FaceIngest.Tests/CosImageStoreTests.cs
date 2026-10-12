using System.Net;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>
/// COS 签名（从主 API 移植的两条，期望值逐字对照 BlazorApp.Api.Tests/TencentCloudUploadServiceTests.cs）与私有上传请求。
/// 全部用假凭据 + 假 HttpMessageHandler，不连真实 COS。
/// </summary>
public sealed class CosImageStoreTests
{
    // 中文注释：与主 API 签名测试同一组假凭据 / 桶名 / 时间，才能逐字比对签名值，证明复制的签名实现没有走样。
    private const string SignatureTestSecretId = "secret-id";
    private const string SignatureTestSecretKey = "secret-key";
    private const string SignatureTestBucket = "hb-sales-2019-1300114625";
    private const string SignatureTestRegion = "ap-singapore";
    private static readonly DateTimeOffset SignatureTestNow = new(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetDirectUploadSignature_使用CosXmlSha1查询签名()
    {
        var signer = new CosRequestSigner(SignatureTestBucket, SignatureTestRegion, SignatureTestSecretId, SignatureTestSecretKey, new FakeTimeProvider(SignatureTestNow));

        var url = signer.GeneratePresignedPutUrl(
            "mobile-app-builds/production/build-123.apk",
            "application/vnd.android.package-archive",
            900);

        var uri = new Uri(url);
        var decodedQuery = Uri.UnescapeDataString(uri.Query.TrimStart('?'));
        Assert.Equal("/mobile-app-builds/production/build-123.apk", uri.AbsolutePath);
        Assert.Equal(
            "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/mobile-app-builds/production/build-123.apk?q-sign-algorithm=sha1&q-ak=secret-id&q-sign-time=1782172800%3B1782173700&q-key-time=1782172800%3B1782173700&q-header-list=content-type%3Bhost&q-url-param-list=&q-signature=8f1d72f7dee4b67d038564b34d71299f8a5ddf52",
            url);
        Assert.Contains("q-sign-algorithm=sha1", decodedQuery, StringComparison.Ordinal);
        Assert.Contains("q-sign-time=1782172800;1782173700", decodedQuery, StringComparison.Ordinal);
        Assert.Contains("q-key-time=1782172800;1782173700", decodedQuery, StringComparison.Ordinal);
        Assert.Contains("q-header-list=content-type;host", decodedQuery, StringComparison.Ordinal);
        Assert.Contains("q-url-param-list=", decodedQuery, StringComparison.Ordinal);
        Assert.Contains("q-signature=8f1d72f7dee4b67d038564b34d71299f8a5ddf52", decodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization=", decodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256", decodedQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDirectUploadSignature_私有Acl和归属元数据参与签名()
    {
        var signer = new CosRequestSigner(SignatureTestBucket, SignatureTestRegion, SignatureTestSecretId, SignatureTestSecretKey, new FakeTimeProvider(SignatureTestNow));

        var url = signer.GeneratePresignedPutUrl(
            "employee-profiles/user-a/identity/token.jpg",
            "image/jpeg",
            900,
            new Dictionary<string, string>
            {
                ["x-cos-acl"] = "private",
                ["x-cos-meta-owner"] = "user-a",
            });

        var query = Uri.UnescapeDataString(new Uri(url).Query);
        Assert.Contains("x-cos-acl", query, StringComparison.Ordinal);
        Assert.Contains("x-cos-meta-owner", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 上传图片_PUT到私有桶_签名并实际发送acl与归属元数据_键按门店日期摄像机()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, etag: "\"fake-etag\"");
        var store = CreateStore(handler, keyPrefix: "face-ingest", uploadRawBody: false);
        var data = HikvisionSamples.FaceJpeg;
        var sha = new string('a', 64);

        var stored = await store.SaveImageAsync(
            new ImageWriteRequest(new string('1', 32), "cam-front-01", "S1001", new DateTimeOffset(2026, 10, 12, 23, 59, 0, TimeSpan.Zero), 1, data, "image/jpeg", sha),
            CancellationToken.None);

        var expectedKey = $"face-ingest/images/S1001/2026/10/12/cam-front-01/{new string('1', 32)}_1.jpg";
        Assert.Equal(new StoredObject("cos", expectedKey), stored);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("fake-bucket-1250000000.cos.ap-singapore.myqcloud.com", request.Uri.Host);
        Assert.Equal("/" + expectedKey, request.Uri.AbsolutePath);
        Assert.Equal("private", request.Headers["x-cos-acl"]);
        Assert.Equal(sha, request.Headers["x-cos-meta-sha256"]);
        Assert.Equal("cam-front-01", request.Headers["x-cos-meta-camera"]);
        Assert.Equal("S1001", request.Headers["x-cos-meta-store"]);
        Assert.Equal("image/jpeg", request.ContentType);
        Assert.Equal(data, request.Body);
        var signedHeaderList = Uri.UnescapeDataString(request.Uri.Query);
        Assert.Contains("q-header-list=content-type;host;x-cos-acl;x-cos-meta-camera;x-cos-meta-sha256;x-cos-meta-store", signedHeaderList, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-secret-key", request.Uri.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, null)]
    [InlineData(HttpStatusCode.Forbidden, "\"fake-etag\"")]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    public async Task 非200或缺ETag_抛异常交给处理器重试_信息不含URL与密钥(HttpStatusCode status, string? etag)
    {
        var handler = new CapturingHandler(status, etag);
        var store = CreateStore(handler, keyPrefix: "face-ingest", uploadRawBody: false);

        var error = await Assert.ThrowsAsync<CosUploadException>(() => store.SaveImageAsync(
            new ImageWriteRequest(new string('2', 32), "cam-front-01", "S1001", DateTimeOffset.UnixEpoch, 1, new byte[] { 0xFF, 0xD8, 0xFF }, "image/jpeg", new string('b', 64)),
            CancellationToken.None));

        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("q-ak", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("myqcloud", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 原始报文默认不上传_开启后按raw键上传()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "\"fake-etag\"");
        var request = new RawBodyWriteRequest(new string('3', 32), "cam-front-01", "S1001", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), new byte[] { 1, 2, 3 }, new string('c', 64));

        Assert.Null(await CreateStore(handler, "face-ingest", uploadRawBody: false).SaveRawBodyAsync(request, CancellationToken.None));
        Assert.Empty(handler.Requests);

        var stored = await CreateStore(handler, "custom/prefix", uploadRawBody: true).SaveRawBodyAsync(request, CancellationToken.None);
        Assert.Equal($"custom/prefix/raw/S1001/2026/01/02/cam-front-01/{new string('3', 32)}.bin", stored!.ObjectKey);
        Assert.Equal("application/octet-stream", Assert.Single(handler.Requests).ContentType);
    }

    private static CosImageStore CreateStore(CapturingHandler handler, string keyPrefix, bool uploadRawBody)
    {
        var options = new FaceIngestOptions
        {
            StorageRootPath = Path.Combine(Path.GetTempPath(), "face-ingest-cos-tests"),
            Cos = new FaceIngestCosOptions
            {
                Enabled = true,
                Bucket = "fake-bucket-1250000000",
                Region = "ap-singapore",
                SecretId = "fake-secret-id",
                SecretKey = "fake-secret-key",
                KeyPrefix = keyPrefix,
                UploadRawBody = uploadRawBody,
            },
        };
        var monitor = new FaceIngestSettingsMonitor(
            new StaticOptionsMonitor(options),
            new CamerasFileLoadErrorRelay(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FaceIngestSettingsMonitor>.Instance);
        return new CosImageStore(new SingleClientFactory(handler), monitor, new FakeTimeProvider(SignatureTestNow));
    }

    private sealed class StaticOptionsMonitor(FaceIngestOptions value) : IOptionsMonitor<FaceIngestOptions>
    {
        public FaceIngestOptions CurrentValue => value;

        public FaceIngestOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<FaceIngestOptions, string?> listener) => null;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? ContentType, byte[] Body);

    /// <summary>记录请求并按设定返回状态码 / ETag 的假 COS。</summary>
    internal sealed class CapturingHandler(HttpStatusCode status, string? etag) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.MediaType, body));
            var response = new HttpResponseMessage(status);
            if (etag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            return response;
        }
    }
}
