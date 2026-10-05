using System.Net;
using System.Net.Http.Json;
using System.Text;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设备 API 响应分类：只有本 API 的 ApiResult 信封才算服务端结论；
/// 网关 HTML、Wi-Fi 认证页、空 200 等一律按"服务不可达"（HttpRequestException）处理。
/// </summary>
public sealed class DeviceApiClientResponseClassificationTests
{
    private const string ActivationCode = "HBDEV1-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string NginxBadGatewayHtml =
        "<html>\r\n<head><title>502 Bad Gateway</title></head>\r\n<body>\r\n<center><h1>502 Bad Gateway</h1></center>\r\n<hr><center>nginx</center>\r\n</body>\r\n</html>\r\n";
    private const string CaptivePortalHtml =
        "<!DOCTYPE html><html><head><title>Wi-Fi Login</title></head><body>Please sign in</body></html>";

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Verify_WithGatewayHtmlError_ThrowsUnavailableTransportFailure(HttpStatusCode statusCode)
    {
        var client = CreateClient(_ => Html(statusCode, NginxBadGatewayHtml));

        var exception = await Record.ExceptionAsync(() => client.VerifyAsync(CreateVerifyRequest()));

        var unavailable = Assert.IsAssignableFrom<HttpRequestException>(exception);
        Assert.Equal(statusCode, unavailable.StatusCode);
    }

    [Fact]
    public async Task Verify_WithCaptivePortalHtmlOn200_ThrowsUnavailableTransportFailure()
    {
        var client = CreateClient(_ => Html(HttpStatusCode.OK, CaptivePortalHtml));

        var exception = await Record.ExceptionAsync(() => client.VerifyAsync(CreateVerifyRequest()));

        var unavailable = Assert.IsAssignableFrom<HttpRequestException>(exception);
        Assert.Equal(HttpStatusCode.OK, unavailable.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "{\"error\":\"upstream unavailable\"}")]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"type\":\"about:blank\",\"title\":\"Too many requests\",\"status\":429}")]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "")]
    [InlineData(HttpStatusCode.RequestTimeout, "")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"ok\"}")]
    [InlineData(HttpStatusCode.OK, "null")]
    public async Task Verify_WithoutApiEnvelopeOnGatewayOrSuccessStatus_ThrowsUnavailableTransportFailure(
        HttpStatusCode statusCode,
        string body)
    {
        var client = CreateClient(_ => Json(statusCode, body));

        var exception = await Record.ExceptionAsync(() => client.VerifyAsync(CreateVerifyRequest()));

        var unavailable = Assert.IsAssignableFrom<HttpRequestException>(exception);
        Assert.Equal(statusCode, unavailable.StatusCode);
    }

    [Fact]
    public async Task Verify_WithApiEnvelopeOn502_ThrowsCatalogApiExceptionWithStatusAndErrorCode()
    {
        var client = CreateClient(_ => Envelope(
            HttpStatusCode.BadGateway,
            ApiResult<DeviceVerifyResponse>.Fail("UPSTREAM_FAILED", "Upstream failed.")));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() => client.VerifyAsync(CreateVerifyRequest()));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal("UPSTREAM_FAILED", exception.ErrorCode);
        Assert.Equal("Upstream failed.", exception.Message);
    }

    [Fact]
    public async Task Verify_WithEnvelopeDataOfIncompatibleShape_ThrowsUnavailableTransportFailure()
    {
        var client = CreateClient(_ => Json(HttpStatusCode.OK, "{\"success\":true,\"data\":\"not-an-object\"}"));

        var exception = await Record.ExceptionAsync(() => client.VerifyAsync(CreateVerifyRequest()));

        Assert.IsAssignableFrom<HttpRequestException>(exception);
    }

    [Fact]
    public async Task Verify_WithParsableNotAllowedResponse_ReturnsServerDecision()
    {
        var client = CreateClient(_ => Envelope(
            HttpStatusCode.OK,
            ApiResult<DeviceVerifyResponse>.Ok(new DeviceVerifyResponse(
                "POS-001",
                "1042",
                "Main Store",
                0,
                false,
                "Device is disabled."))));

        var result = await client.VerifyAsync(CreateVerifyRequest());

        Assert.False(result.IsAllowed);
        Assert.Equal(0, result.DeviceStatus);
    }

    [Fact]
    public async Task Redeem_WithHtml400_DoesNotLookLikeDeterministicBadRequest()
    {
        // 开通码流程把 CatalogApiException(400) 视为确定性拒绝并清除恢复记录；网关 HTML 400 绝不能落入该分支。
        var client = CreateClient(_ => Html(HttpStatusCode.BadRequest, "<html><body>400 Bad Request</body></html>"));

        var exception = await Record.ExceptionAsync(() => client.RedeemActivationCodeAsync(CreateRedeemRequest()));

        Assert.IsNotType<CatalogApiException>(exception);
        var unavailable = Assert.IsAssignableFrom<HttpRequestException>(exception);
        Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
    }

    [Fact]
    public async Task Redeem_WithAspNetValidationProblemDetails400_KeepsCatalogApiBadRequest()
    {
        // ASP.NET 模型校验返回的 ProblemDetails 是 API 自身产生的结构性拒绝，保持原有 400 语义。
        var client = CreateClient(_ => Json(
            HttpStatusCode.BadRequest,
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.1\",\"title\":\"One or more validation errors occurred.\",\"status\":400,\"errors\":{\"activationCode\":[\"invalid\"]}}"));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.RedeemActivationCodeAsync(CreateRedeemRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
    }

    [Fact]
    public async Task Rebind_WithDeviceAuthEnvelope401_KeepsStatusAndErrorCodeForRedeemFallback()
    {
        var client = CreateClient(_ => Envelope(
            HttpStatusCode.Unauthorized,
            ApiResult<object>.Fail("DEVICE_AUTH_REQUIRED", "POS device authorization is required.")));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.RebindActivationCodeAsync(new DeviceActivationCodeRebindRequest(ActivationCode, "POS-TILL-01")));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("DEVICE_AUTH_REQUIRED", exception.ErrorCode);
    }

    [Fact]
    public async Task Rebind_WithEmptyForbidden_KeepsCatalogApiForbidden()
    {
        // 授权策略默认 Forbid 返回空 403；rebind 依赖该状态回退到匿名 redeem 恢复。
        var client = CreateClient(_ => Json(HttpStatusCode.Forbidden, string.Empty));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.RebindActivationCodeAsync(new DeviceActivationCodeRebindRequest(ActivationCode, "POS-TILL-01")));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task Rebind_WithGatewayHtml502_ThrowsUnavailableTransportFailure()
    {
        var client = CreateClient(_ => Html(HttpStatusCode.BadGateway, NginxBadGatewayHtml));

        var exception = await Record.ExceptionAsync(() =>
            client.RebindActivationCodeAsync(new DeviceActivationCodeRebindRequest(ActivationCode, "POS-TILL-01")));

        Assert.IsNotType<CatalogApiException>(exception);
        Assert.IsAssignableFrom<HttpRequestException>(exception);
    }

    [Fact]
    public async Task GetStores_WithCaptivePortalHtml_ThrowsUnavailableTransportFailure()
    {
        var client = CreateClient(_ => Html(HttpStatusCode.OK, CaptivePortalHtml));

        var exception = await Record.ExceptionAsync(() => client.GetStoresAsync());

        Assert.IsAssignableFrom<HttpRequestException>(exception);
    }

    private static DeviceVerifyRequest CreateVerifyRequest() =>
        new("POS-001", "1042", "HW-001", "POS-TILL-01");

    private static DeviceActivationCodeRedeemRequest CreateRedeemRequest() =>
        new(ActivationCode, "HW-001", "POS-TILL-01", DeviceSystems.Windows);

    private static DeviceApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        return new DeviceApiClient(new HttpClient(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://pos.example.test/")
        });
    }

    private static HttpResponseMessage Html(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Envelope<T>(HttpStatusCode statusCode, ApiResult<T> result) =>
        new(statusCode) { Content = JsonContent.Create(result) };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
