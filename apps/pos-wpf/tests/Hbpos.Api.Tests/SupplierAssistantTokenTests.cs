using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.SupplierAssistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

/// <summary>大屏收银 App 内订货助手：设备换中心令牌的 POS API 端。</summary>
public sealed class SupplierAssistantTokenTests
{
    private static readonly SupplierAssistantDeviceIdentity Device = new("POS_1042_0925", "1042", "HW-IPAD-1");

    [Fact]
    public void Controller_只认设备认证()
    {
        var authorize = Assert.Single(typeof(SupplierAssistantController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(DeviceAuthConstants.Scheme, authorize.AuthenticationSchemes);
        Assert.Null(authorize.Policy);
    }

    [Theory]
    [InlineData(DeviceSystems.Windows)]
    [InlineData(DeviceSystems.Android)]
    [InlineData(DeviceSystems.Ios)]
    public async Task Controller_非大屏收银App一律拒绝且不调中心(string deviceSystem)
    {
        var gateway = new RecordingGateway(new SupplierAssistantTokenResult(SupplierAssistantTokenOutcome.Unavailable));
        var controller = CreateController(gateway, deviceSystem);

        var result = await controller.IssueToken(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var body = Assert.IsType<ApiResult<SupplierAssistantTokenResponse>>(objectResult.Value);
        Assert.Equal(SupplierAssistantErrorCodes.DeviceSystemNotSupported, body.ErrorCode);
        Assert.Null(gateway.LastDevice);
    }

    [Theory]
    [InlineData(DeviceSystems.IpadOs)]
    [InlineData(DeviceSystems.AndroidTablet)]
    public async Task Controller_用设备认证里的分店去换令牌(string deviceSystem)
    {
        var expires = new DateTimeOffset(2026, 11, 26, 1, 5, 0, TimeSpan.Zero);
        var gateway = new RecordingGateway(new SupplierAssistantTokenResult(
            SupplierAssistantTokenOutcome.Issued,
            new SupplierAssistantIssuedToken("jwt", expires, "1042", "POS_1042_0925", "https://hotbargain.vip")));
        var controller = CreateController(gateway, deviceSystem);

        var result = await controller.IssueToken(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResult<SupplierAssistantTokenResponse>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal("jwt", body.Data!.AccessToken);
        Assert.Equal("https://hotbargain.vip", body.Data.ApiOrigin);
        Assert.Equal(Device, gateway.LastDevice);
    }

    [Theory]
    [InlineData(SupplierAssistantTokenOutcome.NotEligible, StatusCodes.Status403Forbidden, SupplierAssistantErrorCodes.DeviceNotEligible)]
    [InlineData(SupplierAssistantTokenOutcome.Unavailable, StatusCodes.Status503ServiceUnavailable, SupplierAssistantErrorCodes.Unavailable)]
    public async Task Controller_中心拒绝或不可用时给出对应错误码(
        SupplierAssistantTokenOutcome outcome,
        int statusCode,
        string errorCode)
    {
        var controller = CreateController(
            new RecordingGateway(new SupplierAssistantTokenResult(outcome)),
            DeviceSystems.IpadOs);

        var result = await controller.IssueToken(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        Assert.Equal(errorCode, Assert.IsType<ApiResult<SupplierAssistantTokenResponse>>(objectResult.Value).ErrorCode);
    }

    [Fact]
    public async Task Gateway_带专用服务令牌发往中心并回传公网地址()
    {
        AuthenticationHeaderValue? authorization = null;
        Uri? requestUri = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            authorization = request.Headers.Authorization;
            requestUri = request.RequestUri;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK,
                """{"success":true,"data":{"accessToken":"jwt","accessTokenExpiry":"2026-11-26T01:05:00Z","storeCode":"1042","deviceCode":"POS_1042_0925"}}""");
        });
        var gateway = CreateGateway(handler, new SupplierAssistantOptions
        {
            CenterBaseUrl = "https://center.example/",
            ServiceApiToken = " hbsvc_assistant ",
        });

        var result = await gateway.IssueAsync(Device, CancellationToken.None);

        Assert.Equal(SupplierAssistantTokenOutcome.Issued, result.Outcome);
        Assert.Equal("https://center.example/api/internal/browser-extension/pos-device-tokens", requestUri?.AbsoluteUri);
        Assert.Equal("hbsvc_assistant", authorization?.Parameter);
        Assert.Contains("\"storeCode\":\"1042\"", body, StringComparison.Ordinal);
        Assert.Contains("\"hardwareId\":\"HW-IPAD-1\"", body, StringComparison.Ordinal);
        Assert.Equal("https://center.example", result.Token!.ApiOrigin);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 1, 5, 0, TimeSpan.Zero), result.Token.ExpiresAtUtc);
    }

    [Fact]
    public async Task Gateway_没有配置时不发请求()
    {
        var handler = new StubHandler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException("不应发请求")));
        var gateway = CreateGateway(handler, new SupplierAssistantOptions { CenterBaseUrl = "https://center.example/" });

        var result = await gateway.IssueAsync(Device, CancellationToken.None);

        Assert.Equal(SupplierAssistantTokenOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Gateway_中心地址不是HTTPS时不发服务令牌()
    {
        var handler = new StubHandler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException("不应发请求")));
        var gateway = CreateGateway(handler, new SupplierAssistantOptions
        {
            CenterBaseUrl = "http://center.example/",
            ServiceApiToken = "hbsvc_assistant",
        });

        Assert.Equal(SupplierAssistantTokenOutcome.Unavailable, (await gateway.IssueAsync(Device, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Parse_中心说设备不合格时转成NotEligible()
    {
        var result = HttpSupplierAssistantTokenGateway.ParseCenterResponse(
            HttpStatusCode.Forbidden,
            """{"success":false,"errorCode":"POS_DEVICE_NOT_ELIGIBLE"}""",
            Device,
            "https://hotbargain.vip",
            NullLogger.Instance);

        Assert.Equal(SupplierAssistantTokenOutcome.NotEligible, result.Outcome);
    }

    [Theory]
    [InlineData("1002", "POS_1042_0925")]
    [InlineData("1042", "POS_1042_0001")]
    public void Parse_中心回的分店或设备与本机不符时丢弃(string storeCode, string deviceCode)
    {
        var result = HttpSupplierAssistantTokenGateway.ParseCenterResponse(
            HttpStatusCode.OK,
            $$$"""{"success":true,"data":{"accessToken":"jwt","accessTokenExpiry":"2026-11-26T01:05:00Z","storeCode":"{{{storeCode}}}","deviceCode":"{{{deviceCode}}}"}}""",
            Device,
            "https://hotbargain.vip",
            NullLogger.Instance);

        Assert.Equal(SupplierAssistantTokenOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Token);
    }

    [Fact]
    public void Parse_不带时区的过期时间按UTC解释()
    {
        var result = HttpSupplierAssistantTokenGateway.ParseCenterResponse(
            HttpStatusCode.OK,
            """{"success":true,"data":{"accessToken":"jwt","accessTokenExpiry":"2026-11-26T01:05:00","storeCode":"1042","deviceCode":"POS_1042_0925"}}""",
            Device,
            "https://hotbargain.vip",
            NullLogger.Instance);

        Assert.Equal(new DateTimeOffset(2026, 11, 26, 1, 5, 0, TimeSpan.Zero), result.Token!.ExpiresAtUtc);
    }

    [Fact]
    public void ServiceToken_只读本功能专用的环境变量()
    {
        var requested = new List<string>();

        var token = HttpSupplierAssistantTokenGateway.ResolveServiceToken(
            new SupplierAssistantOptions(),
            name =>
            {
                requested.Add(name);
                return "hbsvc_from_env";
            });

        Assert.Equal("hbsvc_from_env", token);
        Assert.Equal(new[] { SupplierAssistantOptions.ServiceTokenEnvironmentVariable }, requested);
        Assert.Null(HttpSupplierAssistantTokenGateway.ResolveServiceToken(
            new SupplierAssistantOptions { ServiceApiToken = "not-a-service-token" },
            _ => null));
    }

    [Theory]
    [InlineData(null, "https://center.example/base/", "https://center.example")]
    [InlineData("https://api.example:8443/whatever", "https://center.example/", "https://api.example:8443")]
    [InlineData("http://evil.example", "https://center.example/", null)]
    public void PublicApiOrigin_只给HTTPS的origin(string? configured, string center, string? expected)
    {
        Assert.Equal(
            expected,
            HttpSupplierAssistantTokenGateway.ResolvePublicApiOrigin(configured, new Uri(center)));
    }

    private static SupplierAssistantController CreateController(ISupplierAssistantTokenGateway gateway, string deviceSystem)
    {
        var claims = new[]
        {
            new Claim(DeviceAuthConstants.DeviceCodeClaim, Device.DeviceCode),
            new Claim(DeviceAuthConstants.StoreCodeClaim, Device.StoreCode),
            new Claim(DeviceAuthConstants.HardwareIdClaim, Device.HardwareId),
            new Claim(DeviceAuthConstants.DeviceSystemClaim, deviceSystem),
        };
        return new SupplierAssistantController(gateway)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, DeviceAuthConstants.Scheme)),
                },
            },
        };
    }

    private static HttpSupplierAssistantTokenGateway CreateGateway(HttpMessageHandler handler, SupplierAssistantOptions options) =>
        new(
            new HttpClient(handler),
            Options.Create(options),
            Options.Create(new AppUpdateOptions()),
            NullLogger<HttpSupplierAssistantTokenGateway>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingGateway(SupplierAssistantTokenResult result) : ISupplierAssistantTokenGateway
    {
        public SupplierAssistantDeviceIdentity? LastDevice { get; private set; }

        public Task<SupplierAssistantTokenResult> IssueAsync(
            SupplierAssistantDeviceIdentity device,
            CancellationToken cancellationToken)
        {
            LastDevice = device;
            return Task.FromResult(result);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}
