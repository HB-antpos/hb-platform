using System.Net;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class TencentCosMobileAppBuildArtifactMirrorTests
{
    [Fact]
    public async Task MirrorAsync_Artifact缺少ContentLength_拒绝镜像且不上传()
    {
        var artifactHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent([1, 2, 3, 4]),
            }
        );
        var uploadHandler = new CaptureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var mirror = CreateMirror(artifactHandler, uploadHandler);

        var error = await Assert.ThrowsAsync<MobileAppBuildArtifactMirrorException>(() =>
            mirror.MirrorAsync(CreateBuild("https://expo.dev/artifacts/eas/build-no-length.apk"))
        );

        Assert.Contains("Content-Length", error.Message);
        Assert.True(error.IsDownloadUnsafe);
        Assert.Null(uploadHandler.Request);
    }

    [Fact]
    public async Task MirrorAsync_Artifact返回非成功状态_拒绝镜像但允许重试()
    {
        var artifactHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            }
        );
        var uploadHandler = new CaptureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var mirror = CreateMirror(artifactHandler, uploadHandler);

        var error = await Assert.ThrowsAsync<MobileAppBuildArtifactMirrorException>(() =>
            mirror.MirrorAsync(CreateBuild("https://expo.dev/artifacts/eas/build-forbidden.apk"))
        );

        Assert.Contains("HTTP 403", error.Message);
        Assert.False(error.IsDownloadUnsafe);
        Assert.Equal(1, artifactHandler.Calls);
        Assert.Null(uploadHandler.Request);
    }

    [Fact]
    public async Task MirrorAsync_Artifact不是允许域名_拒绝出站请求()
    {
        var artifactHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            }
        );
        var uploadHandler = new CaptureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var mirror = CreateMirror(artifactHandler, uploadHandler);

        var error = await Assert.ThrowsAsync<MobileAppBuildArtifactMirrorException>(() =>
            mirror.MirrorAsync(CreateBuild("https://example.com/build.apk"))
        );

        Assert.Contains("不允许的 APK 下载域名", error.Message);
        Assert.True(error.IsDownloadUnsafe);
        Assert.Equal(0, artifactHandler.Calls);
        Assert.Null(uploadHandler.Request);
    }

    [Fact]
    public async Task MirrorAsync_Artifact重定向到非允许域名_拒绝继续下载()
    {
        var artifactHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://example.com/redirected.apk") },
            }
        );
        var uploadHandler = new CaptureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var mirror = CreateMirror(artifactHandler, uploadHandler);

        var error = await Assert.ThrowsAsync<MobileAppBuildArtifactMirrorException>(() =>
            mirror.MirrorAsync(CreateBuild("https://expo.dev/artifacts/eas/build-redirect.apk"))
        );

        Assert.Contains("不允许的 APK 下载域名", error.Message);
        Assert.True(error.IsDownloadUnsafe);
        Assert.Equal(1, artifactHandler.Calls);
        Assert.Null(uploadHandler.Request);
    }

    [Fact]
    public async Task MirrorAsync_Artifact重定向到EasCdn_允许上传到Cos()
    {
        var artifactHandler = new StubHttpMessageHandler(request =>
            request.RequestUri?.Host == "expo.dev"
                ? new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers =
                    {
                        Location = new Uri("https://wf-artifacts.eascdn.net/build-123.apk"),
                    },
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4]),
                }
        );
        var uploadHandler = new CaptureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var mirror = CreateMirror(artifactHandler, uploadHandler);

        var result = await mirror.MirrorAsync(
            CreateBuild("https://expo.dev/artifacts/eas/build-redirect.apk")
        );

        Assert.Equal(2, artifactHandler.Calls);
        Assert.NotNull(uploadHandler.Request);
        Assert.Equal(HttpMethod.Put, uploadHandler.Request.Method);
        Assert.Equal(
            "hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com",
            uploadHandler.Request.RequestUri?.Host
        );
        Assert.Equal("mobile-app-builds/production/build-123.apk", result.ObjectKey);
    }

    [Fact]
    public async Task MirrorAsync_PosHandheld使用独立AppKey对象路径()
    {
        var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.ContentLength = 4;
        var artifactHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
        );
        var uploadHandler = new CaptureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var mirror = CreateMirror(artifactHandler, uploadHandler);
        var build = CreateBuild("https://expo.dev/artifacts/eas/build-123.apk");
        build.AppKey = "pos-handheld";

        var result = await mirror.MirrorAsync(build);

        Assert.Equal("mobile-app-builds/pos-handheld/production/build-123.apk", result.ObjectKey);
        Assert.Equal(4, result.FileSize);
        Assert.Equal(
            "9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a",
            result.Sha256
        );
    }

    [Fact]
    public async Task ComputeMirroredChecksumAsync_用签名GET读取COS对象计算SHA256与大小()
    {
        var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.ContentLength = 4;
        var cosHandler = new CaptureHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
        );
        var uploadHandler = new CaptureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var mirror = CreateMirror(cosHandler, uploadHandler);

        var checksum = await mirror.ComputeMirroredChecksumAsync(
            CreateMirroredBuild("mobile-app-builds/production/build-123.apk")
        );

        Assert.Equal(4, checksum.FileSize);
        Assert.Equal(
            "9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a",
            checksum.Sha256
        );
        Assert.Equal(HttpMethod.Get, cosHandler.Request!.Method);
        Assert.Equal(
            "hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com",
            cosHandler.Request.RequestUri!.Host
        );
        Assert.Equal("/mobile-app-builds/production/build-123.apk", cosHandler.Request.RequestUri.AbsolutePath);
        Assert.Contains("q-signature=", cosHandler.Request.RequestUri.Query);
        Assert.Null(uploadHandler.Request);
    }

    [Fact]
    public async Task ComputeMirroredChecksumAsync_COS地址与对象键不一致_不发请求()
    {
        var cosHandler = new CaptureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var mirror = CreateMirror(cosHandler, cosHandler);
        var build = CreateMirroredBuild("mobile-app-builds/production/build-123.apk");
        build.CosObjectKey = "mobile-app-builds/pos-handheld/production/build-123.apk";

        var error = await Assert.ThrowsAsync<MobileAppBuildArtifactMirrorException>(() =>
            mirror.ComputeMirroredChecksumAsync(build)
        );

        Assert.Contains("对象键不一致", error.Message);
        Assert.Null(cosHandler.Request);
    }

    [Fact]
    public async Task ComputeMirroredChecksumAsync_实际字节数少于ContentLength_拒绝补算()
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentLength = 4;
        var cosHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
        );
        var mirror = CreateMirror(cosHandler, cosHandler);

        var error = await Assert.ThrowsAsync<MobileAppBuildArtifactMirrorException>(() =>
            mirror.ComputeMirroredChecksumAsync(
                CreateMirroredBuild("mobile-app-builds/production/build-123.apk")
            )
        );

        Assert.Contains("Content-Length", error.Message);
    }

    private static MobileAppBuild CreateMirroredBuild(string objectKey)
    {
        var build = CreateBuild("https://expo.dev/artifacts/eas/build-123.apk");
        build.CosObjectKey = objectKey;
        build.CosArtifactUrl = $"https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/{objectKey}";
        return build;
    }

    private static TencentCosMobileAppBuildArtifactMirror CreateMirror(
        HttpMessageHandler artifactHandler,
        HttpMessageHandler uploadHandler
    )
    {
        var uploadService = new TencentCloudUploadService(
            Options.Create(
                new TencentCloudSettings
                {
                    SecretId = "secret-id",
                    SecretKey = "secret-key",
                    BucketName = "hb-sales-2019-1300114625",
                    Region = "ap-singapore",
                }
            ),
            NullLogger<TencentCloudUploadService>.Instance,
            new HttpClient(uploadHandler)
        );

        return new TencentCosMobileAppBuildArtifactMirror(
            new HttpClient(artifactHandler),
            uploadService,
            NullLogger<TencentCosMobileAppBuildArtifactMirror>.Instance
        );
    }

    private static MobileAppBuild CreateBuild(string artifactUrl)
    {
        return new MobileAppBuild
        {
            EasBuildId = "build-123",
            BuildProfile = "production",
            ArtifactUrl = artifactUrl,
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls += 1;
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class CaptureHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public CaptureHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthContent(byte[] bytes)
        {
            _bytes = bytes;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(_bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
