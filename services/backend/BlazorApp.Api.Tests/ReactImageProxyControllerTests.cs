using System.Net;
using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ReactImageProxyControllerTests
{
    private const string AllowedImageUrl =
        "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/a.png";

    [Fact]
    public void Routes_RequireTheirOwnPermissions()
    {
        var controllerAuthorize = typeof(ReactImageProxyController).GetCustomAttribute<AuthorizeAttribute>();
        var warehouseAuthorize = typeof(ReactImageProxyController)
            .GetMethod(nameof(ReactImageProxyController.Get))!
            .GetCustomAttribute<AuthorizeAttribute>();
        var salesAuthorize = typeof(ReactImageProxyController)
            .GetMethod(nameof(ReactImageProxyController.GetSalesDetail))!
            .GetCustomAttribute<AuthorizeAttribute>();
        var compactBoardAuthorize = typeof(ReactImageProxyController)
            .GetMethod(nameof(ReactImageProxyController.GetCompactSalesBoard))!
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(controllerAuthorize);
        Assert.Equal("Admin,WarehouseManager,WarehouseStaff", warehouseAuthorize?.Roles);
        Assert.Equal(Permissions.SalesDashboard.SalesDetailView, salesAuthorize?.Policy);
        Assert.Equal(Permissions.SalesDashboard.CompactBoardView, compactBoardAuthorize?.Policy);
    }

    [Theory]
    [InlineData("https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/a.jpg", true)]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/a.jpg", true)]
    [InlineData("https://img.supplier.example.com/a.jpg", false)]
    [InlineData("https://localhost/a.jpg", false)]
    public void IsAllowedImageHost_AllowsOnlyConfiguredCosHosts(string url, bool expected)
    {
        var actual = InvokePrivateStatic<bool>("IsAllowedImageHost", new Uri(url));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    public void IsPublicAddress_RejectsLocalPrivateAndLinkLocalAddresses(string address)
    {
        var actual = InvokePrivateStatic<bool>("IsPublicAddress", IPAddress.Parse(address));

        Assert.False(actual);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2001:4860:4860::8888")]
    public void IsPublicAddress_AllowsPublicAddresses(string address)
    {
        var actual = InvokePrivateStatic<bool>("IsPublicAddress", IPAddress.Parse(address));

        Assert.True(actual);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.31.10")]
    [InlineData("169.254.169.254")]
    public async Task IsSafePublicHostAsync_RejectsUnsafeLiteralHosts(string host)
    {
        var actual = await InvokePrivateStaticAsync<bool>(
            "IsSafePublicHostAsync",
            host,
            CancellationToken.None
        );

        Assert.False(actual);
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xd8, 0xff, 0xe0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, "image/png")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0, 0, 0, 0, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 }, "image/avif")]
    public void HasValidImageSignature_AllowsRasterMagicNumbers(byte[] bytes, string contentType)
    {
        var actual = InvokePrivateStatic<bool>("HasValidImageSignature", bytes, contentType);

        Assert.True(actual);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "image/svg+xml")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "image/png")]
    [InlineData("<html><script>alert(1)</script></html>", "image/jpeg")]
    [InlineData("GIF89a", "image/png")]
    public void HasValidImageSignature_RejectsSvgHtmlAndDisguisedContent(string text, string contentType)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var actual = InvokePrivateStatic<bool>("HasValidImageSignature", bytes, contentType);

        Assert.False(actual);
    }

    [Fact]
    public async Task Get_ReturnsImageWithNosniff_WhenRasterImageIsValid()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var controller = CreateController(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pngBytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "image/png"
            );
            return response;
        });

        var result = await controller.Get(AllowedImageUrl);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(pngBytes, file.FileContents);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
    }

    [Fact]
    public async Task Get_RejectsRedirectResponses()
    {
        var controller = CreateController(_ => new HttpResponseMessage(HttpStatusCode.Found));

        var result = await controller.Get(AllowedImageUrl);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("redirect is not allowed", badRequest.Value);
    }

    [Fact]
    public async Task Get_RejectsSvgAndDisguisedHtmlContent()
    {
        var controller = CreateController(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><script>alert(1)</script></html>")
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "image/png"
            );
            return response;
        });

        var result = await controller.Get(AllowedImageUrl);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("not image", notFound.Value);
    }

    [Fact]
    public async Task Get_RejectsImagesOverMaxSize()
    {
        var tooLargePng = new byte[8 * 1024 * 1024 + 1];
        tooLargePng[0] = 0x89;
        tooLargePng[1] = 0x50;
        tooLargePng[2] = 0x4e;
        tooLargePng[3] = 0x47;
        tooLargePng[4] = 0x0d;
        tooLargePng[5] = 0x0a;
        tooLargePng[6] = 0x1a;
        tooLargePng[7] = 0x0a;
        var controller = CreateController(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tooLargePng)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "image/png"
            );
            return response;
        });

        var result = await controller.Get(AllowedImageUrl);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("image too large", badRequest.Value);
    }

    [Fact]
    public async Task GetSalesDetail_StopsUpstreamDownloadWhenBrowserDisconnects()
    {
        var upstreamStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = CreateController(async (_, token) =>
        {
            upstreamStarted.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("上游请求应被取消");
        });
        using var browserAbort = new CancellationTokenSource();
        controller.HttpContext.RequestAborted = browserAbort.Token;

        var request = controller.GetSalesDetail(AllowedImageUrl);
        var upstreamToken = await upstreamStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        browserAbort.Cancel();

        Assert.IsType<EmptyResult>(await request.WaitAsync(AsyncTestWaitSupport.DefaultTimeout));
        Assert.True(upstreamToken.IsCancellationRequested);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(ReactImageProxyController).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        return (T)method!.Invoke(null, args)!;
    }

    private static async Task<T> InvokePrivateStaticAsync<T>(string methodName, params object?[] args)
    {
        var method = typeof(ReactImageProxyController).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        var task = (Task<T>)method!.Invoke(null, args)!;
        return await task;
    }

    private static ReactImageProxyController CreateController(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory
    )
        => CreateController((request, _) => Task.FromResult(responseFactory(request)));

    private static ReactImageProxyController CreateController(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory
    )
    {
        var client = new HttpClient(new StubHttpMessageHandler(responseFactory))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var constructor = typeof(ReactImageProxyController).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(HttpClient),
                typeof(Func<string, CancellationToken, Task<bool>>)
            },
            modifiers: null
        );
        Assert.NotNull(constructor);
        var controller = (ReactImageProxyController)constructor!.Invoke(
            new object[]
            {
                client,
                (Func<string, CancellationToken, Task<bool>>)((_, _) => Task.FromResult(true))
            }
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return _responseFactory(request, cancellationToken);
        }
    }
}
