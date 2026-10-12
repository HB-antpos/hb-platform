using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 读取私有 COS 的 GET：签名请求头格式、配置与回退凭据、状态码 / 超时 / 大小映射、对象键校验、日志不含敏感信息。
/// 全部使用假凭据 + 假 HttpMessageHandler，不连接真实 COS。
/// </summary>
public sealed class FaceImageStoreTests
{
    // 明显的假值：不是任何真实桶、密钥或对象。
    private const string FakeBucket = "fake-face-bucket-1000000000";
    private const string FakeRegion = "ap-fakeland";
    private const string FakeSecretId = "FAKE-DEDICATED-ID";
    private const string FakeSecretKey = "fake-dedicated-key-material";
    private const string MainSecretId = "FAKE-MAIN-ID";
    private const string MainSecretKey = "fake-main-key-material";
    private const string MainBucket = "fake-main-bucket-2000000000";
    private const string FakeHost = "fake-face-bucket-1000000000.cos.ap-fakeland.myqcloud.com";
    private const string ObjectKey = "face-ingest/images/1013/2026/10/13/cam-1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_1.jpg";
    private const int Max = 5 * 1024 * 1024;

    private static readonly DateTimeOffset FixedNow = new(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

    private readonly AuditCapturingLoggerProvider _logs = new();

    // ---------- 签名 ----------

    [Fact]
    public void 签名算法与主API已上线的PUT签名逐字一致_同一组假凭据得到同一个签名()
    {
        // 期望值来自 BlazorApp.Api.Tests/TencentCloudUploadServiceTests 与 FaceIngest 移植测试里同一条 PUT 预签名
        // （q-signature=8f1d72f7...）。请求头形式只是把查询串各段解码后用 & 连起来，所以这里必须逐字相等。
        var authorization = CosAuthorizationSigner.BuildAuthorization(
            "put",
            "mobile-app-builds/production/build-123.apk",
            new Dictionary<string, string>
            {
                ["content-type"] = "application/vnd.android.package-archive",
                ["host"] = "hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com",
            },
            "secret-id",
            "secret-key",
            FixedNow,
            FixedNow.AddSeconds(900));

        Assert.Equal(
            "q-sign-algorithm=sha1&q-ak=secret-id&q-sign-time=1782172800;1782173700&q-key-time=1782172800;1782173700"
            + "&q-header-list=content-type;host&q-url-param-list=&q-signature=8f1d72f7dee4b67d038564b34d71299f8a5ddf52",
            authorization);
    }

    [Fact]
    public void GET签名_只签host_格式与COS文档的请求头签名一致()
    {
        var authorization = CosAuthorizationSigner.BuildAuthorization(
            "get", ObjectKey, new Dictionary<string, string> { ["Host"] = FakeHost }, FakeSecretId, FakeSecretKey, FixedNow, FixedNow.AddMinutes(5));

        // 独立按文档流程再算一遍：HttpString = get\n/{key}\n\nhost={host}\n。
        var keyTime = "1782172800;1782173100";
        var httpString = $"get\n/{ObjectKey}\n\nhost={FakeHost}\n";
        var stringToSign = $"sha1\n{keyTime}\n{Hex(SHA1.HashData(Encoding.UTF8.GetBytes(httpString)))}\n";
        var signKey = Hex(HMACSHA1.HashData(Encoding.UTF8.GetBytes(FakeSecretKey), Encoding.UTF8.GetBytes(keyTime)));
        var signature = Hex(HMACSHA1.HashData(Encoding.UTF8.GetBytes(signKey), Encoding.UTF8.GetBytes(stringToSign)));

        Assert.Equal(
            $"q-sign-algorithm=sha1&q-ak={FakeSecretId}&q-sign-time={keyTime}&q-key-time={keyTime}&q-header-list=host&q-url-param-list=&q-signature={signature}",
            authorization);
    }

    // ---------- 请求形态 ----------

    [Fact]
    public async Task 请求_GET到专用桶域名_只带Authorization头_不带查询串_签名窗口为回拨60秒到5分钟()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));
        var store = CreateStore(handler);

        var bytes = await store.ReadAsync(ObjectKey, Max);

        Assert.Equal(FaceImageTestData.Jpeg, bytes);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal(FakeHost, request.RequestUri.Host);
        Assert.Equal("/" + ObjectKey, request.RequestUri.AbsolutePath);
        Assert.Equal(string.Empty, request.RequestUri.Query);
        var authorization = Assert.Single(request.Headers.GetValues("Authorization"));
        var now = FixedNow.ToUnixTimeSeconds();
        Assert.Matches(
            $"^q-sign-algorithm=sha1&q-ak={FakeSecretId}&q-sign-time={now - 60};{now + 300}&q-key-time={now - 60};{now + 300}&q-header-list=host&q-url-param-list=&q-signature=[0-9a-f]{{40}}$",
            authorization);
        // 除 Authorization 外不带任何自定义头；也没有把签名放进 URL。
        Assert.Equal(["Authorization"], request.Headers.Select(header => header.Key));
        Assert.DoesNotContain("q-sign", request.RequestUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 请求路径逐段编码_对象键里的字符不会改变主机或路径结构()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));

        await CreateStore(handler).ReadAsync("face-ingest/images/S_1/2026/10/13/cam.a-1/x_1.jpg", Max);

        var uri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal(FakeHost, uri.Host);
        Assert.Equal("/face-ingest/images/S_1/2026/10/13/cam.a-1/x_1.jpg", uri.AbsolutePath);
    }

    // ---------- 配置与回退 ----------

    [Theory]
    [InlineData(null, FakeRegion)]
    [InlineData("", FakeRegion)]
    [InlineData("   ", FakeRegion)]
    [InlineData(FakeBucket, null)]
    [InlineData(FakeBucket, "")]
    public async Task 桶或区域没配_503且不发任何请求_也不回退到主桶(string? bucket, string? region)
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));
        var store = CreateStore(handler, cos: new FaceCaptureCosOptions { Bucket = bucket, Region = region, SecretId = FakeSecretId, SecretKey = FakeSecretKey },
            main: new TencentCloudSettings { SecretId = MainSecretId, SecretKey = MainSecretKey, BucketName = MainBucket, Region = "ap-main", ImageBucketName = MainBucket, ImageRegion = "ap-main" });

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => store.ReadAsync(ObjectKey, Max));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("FACE_IMAGE_STORE_NOT_CONFIGURED", ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("evil.com/x")]
    [InlineData("Bucket-UPPER-1")]
    [InlineData("bucket.with.dots-1")]
    [InlineData("bucket:443")]
    [InlineData("-leading-hyphen")]
    [InlineData("bucket_underscore")]
    public async Task 桶名格式不合法_按未配置处理_域名不会被夹带(string bucket)
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() =>
            CreateStore(handler, cos: Cos(bucket: bucket)).ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_STORE_NOT_CONFIGURED", ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 区域格式不合法_按未配置处理()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() =>
            CreateStore(handler, cos: Cos(region: "ap.evil.com")).ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_STORE_NOT_CONFIGURED", ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 没配专用密钥_回退主API的TencentCloud密钥_桶与区域仍用专用配置()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));
        var store = CreateStore(handler,
            cos: Cos(secretId: null, secretKey: null),
            main: new TencentCloudSettings { SecretId = MainSecretId, SecretKey = MainSecretKey, BucketName = MainBucket, Region = "ap-main" });

        await store.ReadAsync(ObjectKey, Max);

        var request = Assert.Single(handler.Requests);
        // 用的是主 API 的密钥，但访问的是专用桶，绝不是主桶。
        Assert.Contains($"q-ak={MainSecretId}", Assert.Single(request.Headers.GetValues("Authorization")), StringComparison.Ordinal);
        Assert.Equal(FakeHost, request.RequestUri!.Host);
        Assert.DoesNotContain(MainBucket, request.RequestUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 配了专用密钥_就用专用密钥_不碰主API密钥()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));
        var store = CreateStore(handler, main: new TencentCloudSettings { SecretId = MainSecretId, SecretKey = MainSecretKey });

        await store.ReadAsync(ObjectKey, Max);

        var authorization = Assert.Single(Assert.Single(handler.Requests).Headers.GetValues("Authorization"));
        Assert.Contains($"q-ak={FakeSecretId}", authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(MainSecretId, authorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FakeSecretId, null)]
    [InlineData(null, FakeSecretKey)]
    [InlineData(FakeSecretId, "  ")]
    public async Task 专用密钥只配了一半_视为配置错误_不混用主API密钥(string? id, string? key)
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));
        var store = CreateStore(handler, cos: Cos(secretId: id, secretKey: key),
            main: new TencentCloudSettings { SecretId = MainSecretId, SecretKey = MainSecretKey });

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => store.ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_STORE_NOT_CONFIGURED", ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 专用密钥和主API密钥都没有_503()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));
        var store = CreateStore(handler, cos: Cos(secretId: null, secretKey: null), main: new TencentCloudSettings());

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => store.ReadAsync(ObjectKey, Max));

        Assert.Equal(503, ex.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SecretId含换行等字符_按配置无效处理_防请求头注入()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));
        var store = CreateStore(handler, cos: Cos(secretId: "id\r\nX-Evil: 1", secretKey: FakeSecretKey));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => store.ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_STORE_NOT_CONFIGURED", ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void 解析后的凭据对象ToString不泄露桶名或密钥()
    {
        var credentials = FaceImageCosConfiguration.Resolve(Cos(), new TencentCloudSettings(), out var problem);

        Assert.NotNull(credentials);
        Assert.Equal(FaceImageCosConfigProblem.None, problem);
        var text = credentials!.ToString();
        foreach (var secret in new[] { FakeSecretId, FakeSecretKey, FakeBucket, FakeRegion })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    // ---------- 状态码映射 ----------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 502, "FACE_IMAGE_STORE_DENIED")]
    [InlineData(HttpStatusCode.Unauthorized, 502, "FACE_IMAGE_STORE_DENIED")]
    [InlineData(HttpStatusCode.NotFound, 404, "FACE_IMAGE_OBJECT_MISSING")]
    [InlineData(HttpStatusCode.InternalServerError, 502, "FACE_IMAGE_STORE_ERROR")]
    [InlineData(HttpStatusCode.ServiceUnavailable, 502, "FACE_IMAGE_STORE_ERROR")]
    [InlineData(HttpStatusCode.BadRequest, 502, "FACE_IMAGE_STORE_ERROR")]
    [InlineData(HttpStatusCode.Found, 502, "FACE_IMAGE_STORE_ERROR")]
    [InlineData(HttpStatusCode.PartialContent, 502, "FACE_IMAGE_STORE_ERROR")]
    public async Task COS返回非200_映射成固定的业务错误_消息不含桶名密钥签名或地址(HttpStatusCode status, int expectedStatus, string expectedCode)
    {
        var body = $"<?xml version='1.0'?><Error><Code>AccessDenied</Code><Message>denied for {FakeBucket}</Message><Resource>{FakeHost}/{ObjectKey}</Resource><RequestId>req-fake</RequestId></Error>";
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
        var store = CreateStore(handler);

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => store.ReadAsync(ObjectKey, Max));

        Assert.Equal(expectedStatus, ex.StatusCode);
        Assert.Equal(expectedCode, ex.Code);
        AssertNoSensitive(ex.Message);
        // 日志里只有状态码与 COS 错误码，没有 COS 错误体的其余内容。
        var warning = Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片读取失败"));
        Assert.Equal((int)status, warning.State["Status"]);
        Assert.Equal("AccessDenied", warning.State["CosCode"]);
        AssertNoSensitive(_logs.Dump());
    }

    [Fact]
    public async Task COS错误体不是XML时_CosCode记为减号_不崩溃()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>nope</html>") }));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateStore(handler).ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_STORE_DENIED", ex.Code);
        Assert.Equal("-", Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片读取失败")).State["CosCode"]);
    }

    // ---------- 超时 / 网络 / 取消 ----------

    [Fact]
    public async Task 响应头迟迟不来_超过总时限_映射成超时错误()
    {
        var handler = new FaceImageCosStubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Ok(FaceImageTestData.Jpeg);
        });
        var store = CreateStore(handler, timeout: TimeSpan.FromMilliseconds(80));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => store.ReadAsync(ObjectKey, Max));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("FACE_IMAGE_STORE_TIMEOUT", ex.Code);
        AssertNoSensitive(ex.Message);
    }

    [Fact]
    public async Task 响应头到了但body一直读不完_同样受总时限约束()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new HangingContent() }));
        var store = CreateStore(handler, timeout: TimeSpan.FromMilliseconds(80));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => store.ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_STORE_TIMEOUT", ex.Code);
    }

    [Fact]
    public async Task HttpClient自身超时抛出的TaskCanceledException_也映射成超时()
    {
        var handler = new FaceImageCosStubHandler((_, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateStore(handler).ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_STORE_TIMEOUT", ex.Code);
    }

    [Fact]
    public async Task 调用方主动取消_不被当成超时_原样抛出取消异常()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FaceImageCosStubHandler(async (_, cancellationToken) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Ok(FaceImageTestData.Jpeg);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateStore(handler).ReadAsync(ObjectKey, Max, cts.Token));
    }

    [Fact]
    public async Task 网络异常_映射成502_日志不记异常消息里的主机名()
    {
        var handler = new FaceImageCosStubHandler((_, _) => throw new HttpRequestException($"Name or service not known ({FakeHost}:443)"));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateStore(handler).ReadAsync(ObjectKey, Max));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("FACE_IMAGE_STORE_ERROR", ex.Code);
        AssertNoSensitive(ex.Message);
        AssertNoSensitive(_logs.Dump());
    }

    // ---------- 大小 ----------

    [Fact]
    public async Task 声明的ContentLength超过上限_不读body直接拒绝()
    {
        var content = new HangingContent { DeclaredLength = Max + 1 };
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateStore(handler).ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_TOO_LARGE", ex.Code);
        Assert.False(content.BodyWasRead);
    }

    [Fact]
    public async Task 没有ContentLength时按实际字节累计_超过上限立即中止()
    {
        // 流式、不声明长度：模拟分块传输或谎报长度。
        var content = new HangingContent { Chunks = Enumerable.Repeat(new byte[1024 * 1024], 7).ToList(), NoLength = true };
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateStore(handler).ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_TOO_LARGE", ex.Code);
        // 读到第 6 个分片（累计超过 5MB）就停，没有把 7 个分片全部读完。
        Assert.True(content.ChunksServed <= 6, $"实际读取了 {content.ChunksServed} 个分片");
    }

    [Fact]
    public async Task 恰好等于上限的图片可以读取()
    {
        var exact = new byte[Max];
        exact[0] = 0xFF;
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(exact)));

        var bytes = await CreateStore(handler).ReadAsync(ObjectKey, Max);

        Assert.Equal(Max, bytes.Length);
    }

    [Fact]
    public async Task 空body_当作读取失败()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok([])));

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateStore(handler).ReadAsync(ObjectKey, Max));

        Assert.Equal("FACE_IMAGE_STORE_ERROR", ex.Code);
    }

    // ---------- 对象键校验 ----------

    [Theory]
    [InlineData("face-ingest/images/1013/2026/10/13/cam-1/aaaa_1.jpg", true)]
    [InlineData("prefix/sub/images/S_1/2026/10/13/cam.1-x/aaaa_1.jpg", true)]
    [InlineData("face-ingest/raw/1013/2026/10/13/cam-1/aaaa.bin", false)]
    [InlineData("face-ingest/images/1013/2026/10/13/cam-1/../../../x.jpg", false)]
    [InlineData("face-ingest/images/1013/2026/10/13/cam-1/./x.jpg", false)]
    [InlineData("../face-ingest/images/1013/2026/10/13/cam-1/x.jpg", false)]
    [InlineData("/face-ingest/images/1013/2026/10/13/cam-1/x.jpg", false)]
    [InlineData("face-ingest/images//2026/10/13/cam-1/x.jpg", false)]
    [InlineData("face-ingest/images/1013/2026/10/13/cam-1/a%2Fb.jpg", false)]
    [InlineData("face-ingest/images/1013/2026/10/13/cam-1/a b.jpg", false)]
    [InlineData("face-ingest/images/1013/2026/10/13/cam-1/a?b=c.jpg", false)]
    [InlineData("face-ingest\\images\\1013\\2026\\10\\13\\cam-1\\x.jpg", false)]
    [InlineData("face-ingest/images/1013/2026/10/13/cam-1/x.jpg\n", false)]
    [InlineData("face-ingest/images/1013/2026/10/13/cam-1", false)]
    [InlineData("images/2026-10-13/cam-1/x.jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 对象键形态校验_只允许images目录下的安全路径(string? key, bool expected)
    {
        Assert.Equal(expected, CosFaceImageStore.IsSafeObjectKey(key));
    }

    [Fact]
    public async Task 库里对象键越界_拒绝读取_不发请求_日志不含键()
    {
        var handler = new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg)));
        const string evil = "face-ingest/images/1013/2026/10/13/cam-1/../../../../../etc/secret.jpg";

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateStore(handler).ReadAsync(evil, Max));

        Assert.Equal("FACE_IMAGE_STORE_ERROR", ex.Code);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("etc/secret", _logs.Dump(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 全流程日志不含密钥_桶名_主机_对象键_签名()
    {
        var handler = new FaceImageCosStubHandler((request, _) =>
        {
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("_1.jpg", StringComparison.Ordinal)
                ? Ok(FaceImageTestData.Jpeg)
                : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<Error><Code>SignatureDoesNotMatch</Code></Error>") });
        });
        var store = CreateStore(handler);

        await store.ReadAsync(ObjectKey, Max);
        await Assert.ThrowsAsync<FaceCameraException>(() => store.ReadAsync(ObjectKey.Replace("_1.jpg", "_2.jpg"), Max));
        await Assert.ThrowsAsync<FaceCameraException>(() => CreateStore(handler, cos: Cos(bucket: null)).ReadAsync(ObjectKey, Max));

        AssertNoSensitive(_logs.Dump());
    }

    // ---------- 注册与部署契约（静态） ----------

    [Fact]
    public void Program注册_专用命名客户端15秒超时_禁用重定向_服务注册齐全()
    {
        var program = File.ReadAllText(RepoFile("services/backend/BlazorApp.Api/Program.cs"));

        Assert.Contains("CosFaceImageStore.HttpClientName", program, StringComparison.Ordinal);
        Assert.Contains("client.Timeout = TimeSpan.FromSeconds(15)", program, StringComparison.Ordinal);
        var start = program.IndexOf("CosFaceImageStore.HttpClientName", StringComparison.Ordinal);
        var block = program[start..Math.Min(program.Length, start + 400)];
        Assert.Contains("AllowAutoRedirect = false", block, StringComparison.Ordinal);
        Assert.Contains("Configure<BlazorApp.Api.Features.FaceCameras.FaceCaptureCosOptions>", program, StringComparison.Ordinal);
        Assert.Contains("FaceCaptureCosOptions.SectionName", program, StringComparison.Ordinal);
        Assert.Equal("FaceCapture:Cos", FaceCaptureCosOptions.SectionName);
        Assert.Contains("IFaceImageStore", program, StringComparison.Ordinal);
        Assert.Contains("IFaceCaptureImageService", program, StringComparison.Ordinal);
    }

    [Fact]
    public void 部署契约_compose里四个FaceCapture_Cos变量都有空默认值_没有一个是必填()
    {
        var compose = File.ReadAllText(RepoFile("services/backend/docker-compose.yml"));

        foreach (var (key, variable) in new[]
                 {
                     ("Bucket", "FACE_CAPTURE_COS_BUCKET"), ("Region", "FACE_CAPTURE_COS_REGION"),
                     ("SecretId", "FACE_CAPTURE_COS_SECRET_ID"), ("SecretKey", "FACE_CAPTURE_COS_SECRET_KEY"),
                 })
        {
            Assert.Contains($"- FaceCapture__Cos__{key}=${{{variable}:-}}", compose, StringComparison.Ordinal);
        }

        // 未配置是合法状态：这些变量缺失不能让主 API 的部署失败。
        Assert.DoesNotMatch(new Regex(@"FACE_CAPTURE_COS_[A-Z_]+:\?"), compose);
        Assert.Single(Regex.Matches(compose, @"FaceCapture__Cos__Bucket"));
    }

    [Fact]
    public async Task 配置节与依赖注入_环境变量式键绑定到FaceCapture_Cos_且未配置时服务可解析并返回503()
    {
        // 与 Program.cs 的注册方式一致：Configure 绑定 FaceCapture:Cos（环境变量 FaceCapture__Cos__* 会映射到同样的键）、
        // 命名 HttpClient、TimeProvider；这里证明构造函数依赖都能解析，空字符串（compose 的 ${VAR:-}）等同未配置。
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FaceCapture:Cos:Bucket"] = FakeBucket,
            ["FaceCapture:Cos:Region"] = FakeRegion,
            ["FaceCapture:Cos:SecretId"] = "",
            ["FaceCapture:Cos:SecretKey"] = "",
            ["TencentCloud:SecretId"] = MainSecretId,
            ["TencentCloud:SecretKey"] = MainSecretKey,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.Configure<FaceCaptureCosOptions>(configuration.GetSection(FaceCaptureCosOptions.SectionName));
        services.Configure<TencentCloudSettings>(configuration.GetSection("TencentCloud"));
        services.AddHttpClient(CosFaceImageStore.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new FaceImageCosStubHandler((_, _) => Task.FromResult(Ok(FaceImageTestData.Jpeg))));
        services.AddSingleton<IFaceImageStore, CosFaceImageStore>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var options = provider.GetRequiredService<IOptions<FaceCaptureCosOptions>>().Value;
        Assert.Equal(FakeBucket, options.Bucket);
        Assert.Equal(FakeRegion, options.Region);
        // compose 里 ${FACE_CAPTURE_COS_SECRET_ID:-} 展开成空串：按「没配专用密钥」处理，回退主 API 密钥。
        var resolved = FaceImageCosConfiguration.Resolve(options, provider.GetRequiredService<IOptions<TencentCloudSettings>>().Value, out _);
        Assert.NotNull(resolved);
        Assert.Equal(MainSecretId, resolved!.SecretId);
        Assert.Equal(FakeHost, resolved.Host);

        var store = provider.GetRequiredService<IFaceImageStore>();
        Assert.Equal(FaceImageTestData.Jpeg, await store.ReadAsync(ObjectKey, Max));
    }

    [Fact]
    public async Task 什么都没配_服务可解析_读取返回503而不是启动失败()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.Configure<FaceCaptureCosOptions>(new ConfigurationBuilder().Build().GetSection(FaceCaptureCosOptions.SectionName));
        services.Configure<TencentCloudSettings>(new ConfigurationBuilder().Build().GetSection("TencentCloud"));
        services.AddHttpClient(CosFaceImageStore.HttpClientName);
        services.AddSingleton<IFaceImageStore, CosFaceImageStore>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => provider.GetRequiredService<IFaceImageStore>().ReadAsync(ObjectKey, Max));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("FACE_IMAGE_STORE_NOT_CONFIGURED", ex.Code);
    }

    // ---------- 辅助 ----------

    private CosFaceImageStore CreateStore(
        FaceImageCosStubHandler handler,
        FaceCaptureCosOptions? cos = null,
        TencentCloudSettings? main = null,
        TimeSpan? timeout = null) =>
        new(
            new FaceImageStubHttpClientFactory(handler),
            Options.Create(cos ?? Cos()),
            Options.Create(main ?? new TencentCloudSettings()),
            new FixedTimeProvider(FixedNow),
            _logs.CreateLogger<CosFaceImageStore>())
        {
            RequestTimeout = timeout ?? CosFaceImageStore.DefaultRequestTimeout,
        };

    private static FaceCaptureCosOptions Cos(
        string? bucket = FakeBucket, string? region = FakeRegion, string? secretId = FakeSecretId, string? secretKey = FakeSecretKey) =>
        new() { Bucket = bucket, Region = region, SecretId = secretId, SecretKey = secretKey };

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>错误信息 / 日志里不得出现：桶名、主机、对象键、密钥、签名参数。</summary>
    private static void AssertNoSensitive(string text)
    {
        foreach (var forbidden in new[]
                 {
                     FakeBucket, FakeHost, FakeRegion, "myqcloud", "face-ingest/images", ObjectKey, FakeSecretId, FakeSecretKey,
                     MainSecretId, MainSecretKey, "q-sign", "q-signature", "Authorization",
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string RepoFile(string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "")
    {
        // 测试文件位于 services/backend/BlazorApp.Api.Tests/，仓库根目录在上三级。
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", ".."));
        return Path.Combine(root, relativePath);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>可编程的假 HTTP 处理器：记录请求并按委托返回响应或抛异常。</summary>
internal sealed class FaceImageCosStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }
}

internal sealed class FaceImageStubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// 可控的响应体：默认永远读不完（用来测 body 阶段的超时）；可声明长度、可分片返回固定字节。
/// </summary>
internal sealed class HangingContent : HttpContent
{
    public long? DeclaredLength { get; init; }
    public bool NoLength { get; init; }
    public List<byte[]>? Chunks { get; init; }
    public bool BodyWasRead { get; private set; }
    public int ChunksServed { get; private set; }

    protected override bool TryComputeLength(out long length)
    {
        length = DeclaredLength ?? 0;
        return DeclaredLength.HasValue && !NoLength;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        BodyWasRead = true;
        if (Chunks == null)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return;
        }

        foreach (var chunk in Chunks)
        {
            ChunksServed++;
            await stream.WriteAsync(chunk, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        BodyWasRead = true;
        Stream stream = Chunks == null ? new HangingStream() : new ChunkStream(this);
        return Task.FromResult(stream);
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class ChunkStream(HangingContent owner) : Stream
    {
        private int _chunk;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var chunks = owner.Chunks!;
            if (_chunk >= chunks.Count) return ValueTask.FromResult(0);
            if (_offset == 0) owner.ChunksServed++;
            var current = chunks[_chunk];
            var count = Math.Min(buffer.Length, current.Length - _offset);
            current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset >= current.Length)
            {
                _chunk++;
                _offset = 0;
            }

            return ValueTask.FromResult(count);
        }
    }
}
