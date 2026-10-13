using System.Net;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Api.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 人脸比对私有对象存储（同意书 / 登记照 / 名单参考照片 / 命中图读取）：PUT / GET / DELETE 的请求头签名、
/// 配置、状态码映射、对象键校验、日志不含敏感信息。假凭据 + 假 HttpMessageHandler，不连接真实 COS。
/// </summary>
public sealed class FaceMatchObjectStoreTests
{
    private const string FakeBucket = "fake-face-bucket-1000000000";
    private const string FakeRegion = "ap-fakeland";
    private const string FakeSecretId = "FAKE-DEDICATED-ID";
    private const string FakeSecretKey = "fake-dedicated-key-material";
    private const string FakeHost = "fake-face-bucket-1000000000.cos.ap-fakeland.myqcloud.com";
    private const string Key = "face-match/enrollment/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/0.jpg";

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 14, 2, 0, 0, TimeSpan.Zero);

    private readonly AuditCapturingLoggerProvider _logs = new();

    [Fact]
    public async Task PUT_带私有ACL头_签名头列表为host与x_cos_acl_只发请求头不生成带签名的URL()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        await CreateStore(handler).PutAsync(Key, FaceImageTestData.Jpeg, "image/jpeg");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(FakeHost, request.RequestUri!.Host);
        Assert.Equal("/" + Key, request.RequestUri.AbsolutePath);
        Assert.Equal(string.Empty, request.RequestUri.Query);
        Assert.Equal("private", Assert.Single(request.Headers.GetValues("x-cos-acl")));
        var authorization = Assert.Single(request.Headers.GetValues("Authorization"));
        var now = FixedNow.ToUnixTimeSeconds();
        Assert.Matches(
            $"^q-sign-algorithm=sha1&q-ak={FakeSecretId}&q-sign-time={now - 60};{now + 300}&q-key-time={now - 60};{now + 300}&q-header-list=host;x-cos-acl&q-url-param-list=&q-signature=[0-9a-f]{{40}}$",
            authorization);
        Assert.Equal("image/jpeg", Assert.Single(handler.ContentTypes));
    }

    [Fact]
    public async Task PUT_失败状态码映射_403为拒绝_5xx为通用错误_日志不含敏感信息()
    {
        var denied = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<Error><Code>AccessDenied</Code><Message>secret detail</Message></Error>"),
        }));
        var ex = await Assert.ThrowsAsync<FaceMatchStoreException>(
            () => CreateStore(denied).PutAsync(Key, FaceImageTestData.Jpeg, "image/jpeg"));
        Assert.Equal(FaceMatchStoreFailureKind.Denied, ex.Kind);

        var broken = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var ex2 = await Assert.ThrowsAsync<FaceMatchStoreException>(
            () => CreateStore(broken).PutAsync(Key, FaceImageTestData.Jpeg, "image/jpeg"));
        Assert.Equal(FaceMatchStoreFailureKind.Error, ex2.Kind);

        var dump = _logs.Dump() + ex.Message + ex2.Message;
        Assert.Contains("AccessDenied", dump, StringComparison.Ordinal);
        AssertNoSensitive(dump);
        Assert.DoesNotContain("secret detail", dump, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GET_读取字节_超过上限拒绝_404为不存在()
    {
        var ok = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FaceImageTestData.Png) }));
        Assert.Equal(FaceImageTestData.Png, await CreateStore(ok).ReadAsync(Key, 1024));

        var tooBig = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) }));
        var big = await Assert.ThrowsAsync<FaceMatchStoreException>(() => CreateStore(tooBig).ReadAsync(Key, 1024));
        Assert.Equal(FaceMatchStoreFailureKind.TooLarge, big.Kind);

        var missing = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var notFound = await Assert.ThrowsAsync<FaceMatchStoreException>(() => CreateStore(missing).ReadAsync(Key, 1024));
        Assert.Equal(FaceMatchStoreFailureKind.NotFound, notFound.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DELETE_成功或对象本来就不存在都视为已删除(HttpStatusCode status)
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));

        await CreateStore(handler).DeleteAsync(Key);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Contains("q-header-list=host&", Assert.Single(request.Headers.GetValues("Authorization")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DELETE_403是拒绝_不被当成已删除()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var ex = await Assert.ThrowsAsync<FaceMatchStoreException>(() => CreateStore(handler).DeleteAsync(Key));

        Assert.Equal(FaceMatchStoreFailureKind.Denied, ex.Kind);
    }

    [Theory]
    [InlineData(null, FakeRegion)]
    [InlineData("", FakeRegion)]
    [InlineData(FakeBucket, null)]
    public async Task 桶或区域没配_不发任何请求_IsConfigured为false(string? bucket, string? region)
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var store = CreateStore(handler, new FaceCaptureCosOptions { Bucket = bucket, Region = region, SecretId = FakeSecretId, SecretKey = FakeSecretKey });

        Assert.False(store.IsConfigured);
        var ex = await Assert.ThrowsAsync<FaceMatchStoreException>(() => store.PutAsync(Key, FaceImageTestData.Jpeg, "image/jpeg"));

        Assert.Equal(FaceMatchStoreFailureKind.NotConfigured, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void 配置完整时IsConfigured为true()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.True(CreateStore(handler).IsConfigured);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("face-match/../secret/x.jpg")]
    [InlineData("face-match//x.jpg")]
    [InlineData("noslash.jpg")]
    [InlineData("face-match/enroll ment/x.jpg")]
    [InlineData("face-match/enrollment/x%2e%2e/y.jpg")]
    [InlineData("")]
    public async Task 对象键形态不合法_一律拒绝且不发请求(string key)
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var store = CreateStore(handler);

        var put = await Assert.ThrowsAsync<FaceMatchStoreException>(() => store.PutAsync(key, FaceImageTestData.Jpeg, "image/jpeg"));
        var read = await Assert.ThrowsAsync<FaceMatchStoreException>(() => store.ReadAsync(key, 1024));
        var del = await Assert.ThrowsAsync<FaceMatchStoreException>(() => store.DeleteAsync(key));

        Assert.All([put, read, del], ex => Assert.Equal(FaceMatchStoreFailureKind.InvalidKey, ex.Kind));
        Assert.Empty(handler.Requests);
    }

    // ---------- 对象键布局 ----------

    [Fact]
    public void 键布局_登记照_同意书_名单参考照片各在独立前缀下()
    {
        var id = new string('a', 32);

        Assert.Equal($"face-match/enrollment/{id}/2.jpg", FaceMatchObjectKeys.Enrollment(id, 2, "jpg"));
        Assert.Equal($"face-match/consent/{id}/scan.pdf", FaceMatchObjectKeys.Consent(id, "pdf"));
        Assert.Equal($"face-match/unwelcome/{id}/0.png", FaceMatchObjectKeys.Unwelcome(id, 0, "png"));
        Assert.All(
            [FaceMatchObjectKeys.Enrollment(id, 0, "jpg"), FaceMatchObjectKeys.Consent(id, "jpg"), FaceMatchObjectKeys.Unwelcome(id, 0, "jpg")],
            key => Assert.True(FaceMatchObjectKeys.IsSafeKey(key)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("../../../etc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void 键布局_编号必须是32位小写十六进制(string id)
    {
        Assert.ThrowsAny<ArgumentException>(() => FaceMatchObjectKeys.Enrollment(id, 0, "jpg"));
    }

    [Theory]
    [InlineData("face-match/hit/1013/aaaaaaaa.jpg", true)]
    [InlineData("face-ingest/images/1013/2026/10/14/cam-1/a_1.jpg", true)]
    [InlineData("face-match/enrollment/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/0.jpg", false)]
    [InlineData("face-match/consent/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/scan.jpg", false)]
    [InlineData("face-match/unwelcome/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/0.jpg", false)]
    [InlineData("face-match/hit/../enrollment/x.jpg", false)]
    [InlineData("", false)]
    public void 命中图键_不允许指向管理端自己的私有前缀(string key, bool allowed)
    {
        Assert.Equal(allowed, FaceMatchObjectKeys.IsAllowedHitImageKey(key));
    }

    // ---------- 辅助 ----------

    private CosFaceMatchObjectStore CreateStore(TencentStubHandler handler, FaceCaptureCosOptions? cos = null) =>
        new(
            new FaceImageStubHttpClientFactory(handler),
            Options.Create(cos ?? new FaceCaptureCosOptions { Bucket = FakeBucket, Region = FakeRegion, SecretId = FakeSecretId, SecretKey = FakeSecretKey }),
            Options.Create(new TencentCloudSettings()),
            new FixedTime(FixedNow),
            _logs.CreateLogger<CosFaceMatchObjectStore>());

    private static void AssertNoSensitive(string text)
    {
        foreach (var forbidden in new[] { FakeBucket, FakeHost, FakeRegion, "myqcloud", Key, FakeSecretId, FakeSecretKey, "q-sign", "Authorization", "face-match/" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
