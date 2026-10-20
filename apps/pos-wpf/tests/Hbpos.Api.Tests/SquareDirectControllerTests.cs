using System.Net;
using System.Reflection;
using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Square;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

// 直连 Square 的两个接口：凭据领取、结账归属登记。门店 / 设备只信设备令牌声明，不读请求体。
public sealed class SquareDirectControllerTests
{
    [Theory]
    [InlineData(nameof(SquareController.GetDirectCredential))]
    [InlineData(nameof(SquareController.RegisterDirectCheckoutOrigin))]
    public void Direct_endpoints_require_the_take_card_policy(string methodName)
    {
        // 令牌下发是整条链路最敏感的出口：必须挂在收卡权限上，不能被误改成匿名或普通登录。
        var authorize = typeof(SquareController).GetMethod(methodName)!
            .GetCustomAttributes<AuthorizeAttribute>()
            .Single();

        Assert.Equal(CashierAuthorizationPolicies.TakeCard, authorize.Policy);
        Assert.Null(typeof(SquareController).GetMethod(methodName)!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task GetDirectCredential_uses_store_and_device_from_claims_not_from_the_request()
    {
        var direct = new StubDirectService();
        var controller = CreateController(direct, store: "S01", device: "POS-01");

        var result = await controller.GetDirectCredential(new SquareDirectCredentialRequest("Production"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResult<SquareDirectCredentialResponse>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal("secret-token", body.Data!.AccessToken);
        Assert.Equal(("Production", "S01", "POS-01"), direct.LastCredentialCall);
    }

    [Fact]
    public async Task GetDirectCredential_maps_disabled_switch_to_403_with_a_stable_code()
    {
        var direct = new StubDirectService
        {
            CredentialException = new SquareTerminalBackendException(
                "SQUARE_DIRECT_DISABLED",
                "Direct Square payment is not enabled for this store.",
                HttpStatusCode.Forbidden)
        };
        var controller = CreateController(direct);

        var result = await controller.GetDirectCredential(new SquareDirectCredentialRequest("Production"), CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        var body = Assert.IsType<ApiResult<SquareDirectCredentialResponse>>(response.Value);
        Assert.False(body.Success);
        Assert.Equal("SQUARE_DIRECT_DISABLED", body.ErrorCode);
        Assert.Null(body.Data);
    }

    [Fact]
    public async Task GetDirectCredential_maps_rate_limit_to_429()
    {
        var direct = new StubDirectService
        {
            CredentialException = new SquareTerminalBackendException(
                "SQUARE_DIRECT_RATE_LIMITED", "Too many credential requests.", HttpStatusCode.TooManyRequests)
        };
        var controller = CreateController(direct);

        var result = await controller.GetDirectCredential(new SquareDirectCredentialRequest("Production"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task GetDirectCredential_without_device_claims_is_forbidden_and_never_reaches_the_service()
    {
        var direct = new StubDirectService();
        var controller = CreateController(direct, store: null, device: null);

        var result = await controller.GetDirectCredential(new SquareDirectCredentialRequest("Production"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Null(direct.LastCredentialCall);
    }

    [Fact]
    public async Task GetDirectCredential_returns_503_when_the_direct_service_is_not_registered()
    {
        var controller = CreateController(direct: null);

        var result = await controller.GetDirectCredential(new SquareDirectCredentialRequest("Production"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task RegisterDirectCheckoutOrigin_passes_claim_scope_and_returns_the_bound_origin()
    {
        var direct = new StubDirectService();
        var controller = CreateController(direct, store: "S01", device: "POS-01");

        var result = await controller.RegisterDirectCheckoutOrigin("chk-1", new SquareDirectOriginRequest("Production"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResult<SquareDirectOriginResponse>>(ok.Value);
        Assert.Equal("chk-1", body.Data!.CheckoutId);
        Assert.Equal(("Production", "chk-1", "S01", "POS-01"), direct.LastOriginCall);
    }

    [Fact]
    public async Task RegisterDirectCheckoutOrigin_maps_mismatch_to_403()
    {
        var direct = new StubDirectService
        {
            OriginException = new SquareTerminalBackendException(
                "SQUARE_DIRECT_ORIGIN_MISMATCH", "Square checkout does not belong to the calling POS device.", HttpStatusCode.Forbidden)
        };
        var controller = CreateController(direct);

        var result = await controller.RegisterDirectCheckoutOrigin("chk-1", new SquareDirectOriginRequest("Production"), CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        Assert.Equal("SQUARE_DIRECT_ORIGIN_MISMATCH", Assert.IsType<ApiResult<SquareDirectOriginResponse>>(response.Value).ErrorCode);
    }

    [Fact]
    public async Task RegisterDirectCheckoutOrigin_maps_square_upstream_failure_to_502_without_leaking_details()
    {
        var direct = new StubDirectService
        {
            OriginException = new SquareTerminalRestException(HttpStatusCode.Unauthorized, "Bearer EAAAsecret rejected")
        };
        var controller = CreateController(direct);

        var result = await controller.RegisterDirectCheckoutOrigin("chk-1", new SquareDirectOriginRequest("Production"), CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, response.StatusCode);
        var body = Assert.IsType<ApiResult<SquareDirectOriginResponse>>(response.Value);
        Assert.DoesNotContain("EAAAsecret", body.Message ?? string.Empty, StringComparison.Ordinal);
    }

    private static SquareController CreateController(ISquareDirectService? direct, string? store = "S01", string? device = "POS-01")
    {
        var claims = new List<Claim>();
        if (store is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.StoreCodeClaim, store));
        }

        if (device is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.DeviceCodeClaim, device));
        }

        var controller = new SquareController(
            new UnusedTokenService(),
            new UnusedBackendService(),
            NullLogger<SquareController>.Instance,
            checkoutSessionRepository: null,
            directService: direct);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };
        return controller;
    }

    private sealed class UnusedTokenService : ISquareTokenService
    {
        public Task<SquareTokenResponse?> GetActiveTokenAsync(string environment, CancellationToken cancellationToken) =>
            throw new NotSupportedException("direct endpoints delegate token reads to the direct service");
    }

    private sealed class UnusedBackendService : ISquareTerminalBackendService
    {
        public Task<IReadOnlyList<SquareLocationDto>> GetLocationsAsync(string environment, CancellationToken cancellationToken) => throw Unexpected();

        public Task<IReadOnlyList<SquareDeviceDto>> GetDevicesAsync(string environment, string locationId, CancellationToken cancellationToken) => throw Unexpected();

        public Task<IReadOnlyList<SquareDeviceCodeDto>> GetDeviceCodesAsync(string environment, string locationId, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareDeviceCodeDto> CreateDeviceCodeAsync(SquareCreateDeviceCodeRequest request, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareDeviceCodeDto?> GetDeviceCodeAsync(string environment, string deviceCodeId, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareCheckoutStatusResponse> CreateCheckoutAsync(SquareCreateCheckoutRequest request, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareCheckoutStatusResponse?> GetCheckoutAsync(string environment, string checkoutId, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquarePaymentStatusDto?> GetPaymentAsync(string environment, string paymentId, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareCheckoutStatusResponse> CancelCheckoutAsync(string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareCheckoutStatusResponse> DismissCheckoutAsync(string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareRefundResponse> CreateRefundAsync(SquareRefundRequest request, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareRefundResponse?> GetRefundAsync(string environment, string refundId, CancellationToken cancellationToken) => throw Unexpected();

        public Task<SquareWebhookAcceptedResponse> AcceptWebhookAsync(SquareWebhookRequest request, CancellationToken cancellationToken) => throw Unexpected();

        private static NotSupportedException Unexpected() => new("direct endpoints must not use the relay backend service");
    }

    private sealed class StubDirectService : ISquareDirectService
    {
        public (string Environment, string Store, string Device)? LastCredentialCall { get; private set; }

        public (string Environment, string CheckoutId, string Store, string Device)? LastOriginCall { get; private set; }

        public Exception? CredentialException { get; init; }

        public Exception? OriginException { get; init; }

        public Task<SquareDirectCredentialResponse> IssueCredentialAsync(string environment, string storeCode, string deviceCode, CancellationToken cancellationToken)
        {
            if (CredentialException is not null)
            {
                throw CredentialException;
            }

            LastCredentialCall = (environment, storeCode, deviceCode);
            return Task.FromResult(new SquareDirectCredentialResponse(environment, "secret-token", 1, DateTimeOffset.UtcNow.AddHours(12)));
        }

        public Task<SquareDirectOriginResponse> RegisterOriginAsync(string environment, string checkoutId, string storeCode, string deviceCode, CancellationToken cancellationToken)
        {
            if (OriginException is not null)
            {
                throw OriginException;
            }

            LastOriginCall = (environment, checkoutId, storeCode, deviceCode);
            return Task.FromResult(new SquareDirectOriginResponse(checkoutId, environment, storeCode, deviceCode));
        }

        public Task RecordServerRefundAsync(string environment, string refundId, string? paymentId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CheckRefundNotificationAsync(string environment, string refundId, string? paymentId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
