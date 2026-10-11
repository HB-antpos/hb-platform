using System.Net;
using System.Net.Http;
using System.Text;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class MobileAppDownloadServiceTests
{
    private const string IosJson =
        """{"success":true,"data":{"state":"required","latestVersion":"1.0.7","appStoreUrl":"https://apps.apple.com/au/app/hbgroup/id6786073002?uo=4"}}""";

    private const string AndroidJson =
        """{"success":true,"data":{"appVersion":"1.0.10","appBuildVersion":"63","artifactSize":124168826,"artifactUrl":"https://example.test/a.apk"}}""";

    [Fact]
    public void Local_links_need_no_network_and_point_at_the_public_endpoints()
    {
        var service = CreateService(_ => throw new InvalidOperationException("本地链接不应发请求"));

        var info = service.GetLocalLinks();

        Assert.Equal(MobileAppDownloadService.FallbackIosAppStoreUrl, info.IosUrl);
        Assert.Equal(
            "https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production",
            info.AndroidUrl);
        Assert.Equal(MobileAppAndroidAvailability.Unknown, info.AndroidAvailability);
        Assert.False(info.MetadataLoaded);
        Assert.Null(info.IosVersion);
        Assert.Null(info.AndroidVersion);
    }

    [Fact]
    public async Task Refresh_reads_the_store_link_versions_and_package_size()
    {
        var requested = new List<string>();
        var service = CreateService(request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);
            return Json(request.RequestUri!.AbsolutePath.Contains("mobile-ios", StringComparison.Ordinal) ? IosJson : AndroidJson);
        });

        var info = await service.RefreshAsync();

        Assert.True(info.MetadataLoaded);
        Assert.Equal("https://apps.apple.com/au/app/hbgroup/id6786073002?uo=4", info.IosUrl);
        Assert.Equal("1.0.7", info.IosVersion);
        Assert.Equal(MobileAppAndroidAvailability.Available, info.AndroidAvailability);
        Assert.Equal("1.0.10", info.AndroidVersion);
        Assert.Equal("63", info.AndroidBuild);
        Assert.Equal(124_168_826, info.AndroidSizeBytes);
        // 二维码仍是固定的下载入口，不是 JSON 里的 artifactUrl（那个地址会过期）。
        Assert.Equal(service.GetLocalLinks().AndroidUrl, info.AndroidUrl);
        Assert.Contains("/api/app-updates/mobile-ios?version=0.0.0&build=0", requested);
        Assert.Contains("/api/mobile-app-builds/android-latest?profile=production&integrity=sha256-v1", requested);
    }

    [Fact]
    public async Task Refresh_marks_the_android_package_as_not_published_when_the_center_has_none()
    {
        var service = CreateService(request =>
            Json(request.RequestUri!.AbsolutePath.Contains("mobile-ios", StringComparison.Ordinal)
                ? IosJson
                : """{"success":true,"data":null}"""));

        var info = await service.RefreshAsync();

        Assert.Equal(MobileAppAndroidAvailability.NotPublished, info.AndroidAvailability);
        Assert.Null(info.AndroidVersion);
        Assert.Null(info.AndroidSizeBytes);
        Assert.True(info.MetadataLoaded);
    }

    [Theory]
    [InlineData("https://evil.example/app")]
    [InlineData("http://apps.apple.com/au/app/hbgroup/id6786073002")]
    [InlineData("not a url")]
    public async Task Refresh_ignores_store_links_that_are_not_https_apple_store_addresses(string storeUrl)
    {
        var service = CreateService(request =>
            Json(request.RequestUri!.AbsolutePath.Contains("mobile-ios", StringComparison.Ordinal)
                ? $$$"""{"success":true,"data":{"latestVersion":"1.0.7","appStoreUrl":"{{{storeUrl}}}"}}"""
                : AndroidJson));

        var info = await service.RefreshAsync();

        // 二维码内容来自网络响应，必须只接受 Apple 商店的 https 地址，否则退回固定链接。
        Assert.Equal(MobileAppDownloadService.FallbackIosAppStoreUrl, info.IosUrl);
        Assert.Equal("1.0.7", info.IosVersion);
    }

    [Fact]
    public async Task Refresh_keeps_the_ios_result_when_only_the_android_lookup_fails()
    {
        var service = CreateService(request =>
            request.RequestUri!.AbsolutePath.Contains("mobile-ios", StringComparison.Ordinal)
                ? Json(IosJson)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var info = await service.RefreshAsync();

        Assert.True(info.MetadataLoaded);
        Assert.Equal("1.0.7", info.IosVersion);
        Assert.Equal(MobileAppAndroidAvailability.Unknown, info.AndroidAvailability);
        Assert.Null(info.AndroidVersion);
    }

    [Fact]
    public async Task Refresh_falls_back_to_the_local_links_when_the_network_is_down()
    {
        var service = CreateService(_ => throw new HttpRequestException("offline"));

        var info = await service.RefreshAsync();

        Assert.False(info.MetadataLoaded);
        Assert.Equal(service.GetLocalLinks(), info);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"success":false,"data":null}""")]
    [InlineData("""[]""")]
    public async Task Refresh_treats_unusable_responses_as_offline(string body)
    {
        var service = CreateService(_ => Json(body));

        var info = await service.RefreshAsync();

        Assert.False(info.MetadataLoaded);
        Assert.Equal(MobileAppAndroidAvailability.Unknown, info.AndroidAvailability);
        Assert.Equal(MobileAppDownloadService.FallbackIosAppStoreUrl, info.IosUrl);
    }

    [Fact]
    public async Task Refresh_propagates_cancellation_requested_by_the_caller()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new MobileAppDownloadService(new HttpClient(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        })) { BaseAddress = new Uri("https://hotbargain.vip/") });

        var refresh = service.RefreshAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public void The_configured_center_address_is_used_for_the_android_download_link()
    {
        var service = new MobileAppDownloadService(
            new HttpClient(new StubHandler((_, _) => Task.FromResult(Json("{}")))) { BaseAddress = new Uri("https://center.example/") });

        Assert.StartsWith(
            "https://center.example/api/mobile-app-builds/android-latest/download",
            service.GetLocalLinks().AndroidUrl,
            StringComparison.Ordinal);
    }

    private static MobileAppDownloadService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler((request, _) => Task.FromResult(responder(request))))
        {
            BaseAddress = new Uri("https://hotbargain.vip/")
        });

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
