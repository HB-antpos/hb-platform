using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Hbpos.Api;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Square;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class SquareControllerTests
{
    private const string BackendToken = "opaque-api-square-token";

    [Fact]
    public void SquareTokenEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("token", typeof(SquareController)
            .GetMethod(nameof(SquareController.GetToken))?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template);
        Assert.NotNull(typeof(SquareController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public void SquareTerminalEndpoints_KeepExpectedRouteTemplates()
    {
        AssertHttpTemplate("GetLocations", typeof(HttpGetAttribute), "locations");
        AssertHttpTemplate("GetDevices", typeof(HttpGetAttribute), "devices");
        AssertHttpTemplate("GetDeviceCodes", typeof(HttpGetAttribute), "device-codes");
        AssertHttpTemplate("CreateDeviceCode", typeof(HttpPostAttribute), "device-codes");
        AssertHttpTemplate("GetDeviceCode", typeof(HttpGetAttribute), "device-codes/{deviceCodeId}");
        AssertHttpTemplate("CreateCheckout", typeof(HttpPostAttribute), "checkouts");
        AssertHttpTemplate("GetCheckout", typeof(HttpGetAttribute), "checkouts/{checkoutId}");
        AssertHttpTemplate("GetPayment", typeof(HttpGetAttribute), "payments/{paymentId}");
        AssertHttpTemplate("CancelCheckout", typeof(HttpPostAttribute), "checkouts/{checkoutId}/cancel");
        AssertHttpTemplate("DismissCheckout", typeof(HttpPostAttribute), "checkouts/{checkoutId}/dismiss");
        AssertHttpTemplate("CreateRefund", typeof(HttpPostAttribute), "refunds");
        AssertHttpTemplate("ReceiveWebhook", typeof(HttpPostAttribute), "webhooks");

        Assert.NotNull(typeof(SquareController)
            .GetMethod("ReceiveWebhook")?
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Theory]
    [InlineData(nameof(SquareController.GetCheckout))]
    [InlineData(nameof(SquareController.GetPayment))]
    [InlineData(nameof(SquareController.CancelCheckout))]
    [InlineData(nameof(SquareController.DismissCheckout))]
    public void SquareContinuationEndpoints_UseDeviceScopeInsteadOfStartingNewCashierOperation(string methodName)
    {
        var policies = typeof(SquareController)
            .GetMethod(methodName)!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy)
            .ToArray();

        Assert.DoesNotContain(CashierAuthorizationPolicies.TakeCard, policies);
    }

    [Fact]
    public void SquareCreateCheckoutRequest_RequiresLocationId()
    {
        var request = new SquareCreateCheckoutRequest(
            "Production",
            "idem-001",
            "device-001",
            "location-001",
            new SquareMoneyDto(1299, "AUD"));

        Assert.Equal("location-001", request.LocationId);
    }

    [Fact]
    public async Task GetToken_RequiresAuthentication()
    {
        await using var factory = new SquareApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/square/token?environment=Production");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetLocations_RequiresAuthentication()
    {
        await using var factory = new SquareApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/square/locations?environment=Production");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetToken_ReturnsStatusWithoutAccessToken()
    {
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 5, 26, 4, 0, 0, TimeSpan.Zero));
        string? requestedEnvironment = null;

        await using var factory = new SquareApiFactory(new StubSquareTokenService(
            responseFactory: environment =>
            {
                requestedEnvironment = environment;
                return Task.FromResult<SquareTokenResponse?>(expected);
            }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/token?environment=production");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawContent = await response.Content.ReadAsStringAsync();
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareTokenStatusResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult.Success);
        Assert.Equal(expected.Environment, apiResult.Data?.Environment);
        Assert.Equal(expected.UpdatedAt, apiResult.Data?.UpdatedAt);
        Assert.True(apiResult.Data?.Configured);
        Assert.True(apiResult.Data?.Enabled);
        Assert.DoesNotContain(BackendToken, rawContent, StringComparison.Ordinal);
        Assert.Equal("Production", requestedEnvironment);
    }

    [Fact]
    public async Task GetToken_ReturnsBadRequestForInvalidEnvironment()
    {
        await using var factory = new SquareApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/token?environment=staging");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareTokenStatusResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_ENVIRONMENT_INVALID", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetLocations_ReturnsBadRequestForInvalidEnvironment()
    {
        await using var factory = new SquareApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/locations?environment=staging");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<JsonElement>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_ENVIRONMENT_INVALID", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetToken_ReturnsStableNotFoundWhenTokenIsMissing()
    {
        await using var factory = new SquareApiFactory(new StubSquareTokenService(
            responseFactory: _ => Task.FromResult<SquareTokenResponse?>(null)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/token?environment=Sandbox");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareTokenStatusResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_TOKEN_NOT_CONFIGURED", apiResult.ErrorCode);
        Assert.Equal("Square token is not configured for this environment.", apiResult.Message);
    }

    [Fact]
    public async Task GetLocations_ReturnsStableNotFoundWhenTokenIsMissing()
    {
        await using var factory = new SquareApiFactory(new StubSquareTokenService(
            responseFactory: _ => Task.FromResult<SquareTokenResponse?>(null)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/locations?environment=Sandbox");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<JsonElement>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_TOKEN_NOT_CONFIGURED", apiResult.ErrorCode);
        Assert.Equal("Square token is not configured for this environment.", apiResult.Message);
        Assert.DoesNotContain(BackendToken, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_FailsWhenSquareBackendServiceIsMissing()
    {
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 5, 26, 4, 0, 0, TimeSpan.Zero));

        using var factory = new SquareApiFactory(
            new StubSquareTokenService(responseFactory: _ => Task.FromResult<SquareTokenResponse?>(expected)),
            registerBackendService: false);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(nameof(ISquareTerminalBackendService), exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(BackendToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_FailsWhenSquareApiVersionIsInvalid()
    {
        using var factory = new SquareApiFactory(squareApiVersion: "20260520");

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Square:ApiVersion must use yyyy-MM-dd.", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(BackendToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_ReturnsSanitizedServerErrorWhenServiceThrows()
    {
        var squareLogger = new RecordingLogger<SquareController>();
        await using var factory = new SquareApiFactory(new StubSquareTokenService(
            exceptionFactory: _ => new InvalidOperationException($"SQL timeout from POSM_SquareToken on server db01 for token {BackendToken}")),
            squareLogger: squareLogger);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/token?environment=Production");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareTokenStatusResponse>>();
        Assert.NotNull(apiResult);
        var result = apiResult!;
        Assert.False(result.Success);
        Assert.Equal("SQUARE_TOKEN_READ_FAILED", result.ErrorCode);
        var message = result.Message ?? string.Empty;
        Assert.Equal("Failed to load Square token configuration.", message);
        Assert.DoesNotContain("SQL", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("POSM_SquareToken", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db01", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            !message.Contains(BackendToken, StringComparison.Ordinal),
            "sanitized API error should not include token values");
        Assert.Contains(squareLogger.Lines, line =>
            line.Contains("token-status", StringComparison.Ordinal) &&
            line.Contains("SQUARE_TOKEN_READ_FAILED", StringComparison.Ordinal) &&
            line.Contains("InvalidOperationException", StringComparison.Ordinal) &&
            line.Contains("token [REDACTED]", StringComparison.Ordinal));
        Assert.All(squareLogger.Lines, line =>
            Assert.DoesNotContain(BackendToken, line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetLocations_ReturnsStableBackendFailureWithoutLeakingToken()
    {
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 5, 26, 4, 0, 0, TimeSpan.Zero));

        var squareLogger = new RecordingLogger<SquareController>();
        await using var factory = new SquareApiFactory(
            new StubSquareTokenService(responseFactory: _ => Task.FromResult<SquareTokenResponse?>(expected)),
            backendService: new ThrowingSquareTerminalBackendService(
                () => new SquareTerminalBackendException(
                    "SQUARE_BACKEND_REQUEST_FAILED",
                    "token opaque-api-square-token should never leak",
                    HttpStatusCode.ServiceUnavailable)),
            squareLogger: squareLogger);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/locations?environment=Production");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var rawContent = await response.Content.ReadAsStringAsync();
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<JsonElement>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_BACKEND_REQUEST_FAILED", apiResult.ErrorCode);
        Assert.Equal("Square backend request failed.", apiResult.Message);
        Assert.DoesNotContain(BackendToken, rawContent, StringComparison.Ordinal);
        Assert.Contains(squareLogger.Lines, line =>
            line.Contains("locations", StringComparison.Ordinal) &&
            line.Contains("SQUARE_BACKEND_REQUEST_FAILED", StringComparison.Ordinal) &&
            line.Contains("503", StringComparison.Ordinal) &&
            line.Contains("SquareTerminalBackendException", StringComparison.Ordinal) &&
            line.Contains("token [REDACTED]", StringComparison.Ordinal));
        Assert.All(squareLogger.Lines, line =>
            Assert.DoesNotContain(BackendToken, line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetLocations_ReturnsStableUpstreamFailureWithoutLeakingToken()
    {
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 5, 26, 4, 0, 0, TimeSpan.Zero));

        var squareLogger = new RecordingLogger<SquareController>();
        await using var factory = new SquareApiFactory(
            new StubSquareTokenService(responseFactory: _ => Task.FromResult<SquareTokenResponse?>(expected)),
            backendService: new ThrowingSquareTerminalBackendService(
                () => new SquareTerminalRestException(
                    HttpStatusCode.BadGateway,
                    $"Square upstream exploded with token {BackendToken}")),
            squareLogger: squareLogger);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/locations?environment=Production");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var rawContent = await response.Content.ReadAsStringAsync();
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<JsonElement>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_UPSTREAM_REQUEST_FAILED", apiResult.ErrorCode);
        Assert.Equal("Square upstream request failed.", apiResult.Message);
        Assert.DoesNotContain(BackendToken, rawContent, StringComparison.Ordinal);
        Assert.Contains(squareLogger.Lines, line =>
            line.Contains("locations", StringComparison.Ordinal) &&
            line.Contains("SQUARE_UPSTREAM_REQUEST_FAILED", StringComparison.Ordinal) &&
            line.Contains("502", StringComparison.Ordinal) &&
            line.Contains("SquareTerminalRestException", StringComparison.Ordinal) &&
            line.Contains("token [REDACTED]", StringComparison.Ordinal));
        Assert.All(squareLogger.Lines, line =>
            Assert.DoesNotContain(BackendToken, line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateDeviceCode_ReturnsBadRequestWhenIdempotencyKeyIsMissing()
    {
        var tokenRequested = false;
        var backendService = new CapturingSquareTerminalBackendService();
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 5, 26, 4, 0, 0, TimeSpan.Zero));

        await using var factory = new SquareApiFactory(
            new StubSquareTokenService(responseFactory: _ =>
            {
                tokenRequested = true;
                return Task.FromResult<SquareTokenResponse?>(expected);
            }),
            backendService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/square/device-codes",
            new SquareCreateDeviceCodeRequest(
                "Production",
                IdempotencyKey: "",
                LocationId: "location-001",
                Name: "Front Counter"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareDeviceCodeDto>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_IDEMPOTENCY_KEY_REQUIRED", apiResult.ErrorCode);
        Assert.Equal("idempotencyKey is required.", apiResult.Message);
        Assert.False(tokenRequested);
        Assert.Equal(0, backendService.CreateDeviceCodeCalls);
    }

    [Fact]
    public async Task CreateCheckout_ReturnsBadRequestBeforeReadingTokenWhenIdempotencyKeyIsMissing()
    {
        var tokenRequested = false;
        var backendService = new CapturingSquareTerminalBackendService();
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 5, 26, 4, 0, 0, TimeSpan.Zero));

        await using var factory = new SquareApiFactory(
            new StubSquareTokenService(responseFactory: _ =>
            {
                tokenRequested = true;
                return Task.FromResult<SquareTokenResponse?>(expected);
            }),
            backendService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/square/checkouts",
            new SquareCreateCheckoutRequest(
                "Production",
                IdempotencyKey: " ",
                DeviceId: "device-001",
                LocationId: "location-001",
                AmountMoney: new SquareMoneyDto(1299, "AUD")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareCheckoutStatusResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_IDEMPOTENCY_KEY_REQUIRED", apiResult.ErrorCode);
        Assert.Equal("idempotencyKey is required.", apiResult.Message);
        Assert.False(tokenRequested);
        Assert.Equal(0, backendService.CreateCheckoutCalls);
    }

    [Fact]
    public async Task CreateCheckout_PersistsAuthenticatedDeviceOrigin()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        var backendService = new CapturingSquareTerminalBackendService();
        var controller = CreateController(repository, backendService);

        var result = await controller.CreateCheckout(
            new SquareCreateCheckoutRequest(
                "Production",
                "idem-origin-001",
                "square-device-001",
                "location-001",
                new SquareMoneyDto(1299, "AUD")),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var session = Assert.Single(repository.Sessions);
        Assert.Equal("S01", session.OriginStoreCode);
        Assert.Equal("POS-01", session.OriginDeviceCode);
    }

    [Fact]
    public async Task CreateCheckout_WhenIdempotentCheckoutBelongsToAnotherDevice_DoesNotMoveOrigin()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        await repository.UpsertCheckoutSessionAsync(
            CreateCheckoutSession("S01", "POS-02"),
            CancellationToken.None);
        var controller = CreateController(repository, new CapturingSquareTerminalBackendService());

        var result = await controller.CreateCheckout(
            new SquareCreateCheckoutRequest(
                "Production",
                "idem-origin-conflict",
                "square-device-001",
                "location-001",
                new SquareMoneyDto(1299, "AUD")),
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("POS-02", Assert.Single(repository.Sessions).OriginDeviceCode);
    }

    [Fact]
    public async Task GetCheckout_WithSameDeviceClaimsAndNoCashierToken_AllowsContinuation()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        await repository.UpsertCheckoutSessionAsync(
            CreateCheckoutSession("S01", "POS-01"),
            CancellationToken.None);
        var backendService = new CapturingSquareTerminalBackendService();
        var controller = CreateController(repository, backendService);

        var result = await controller.GetCheckout("checkout-001", "Production", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, backendService.GetCheckoutCalls);
    }

    [Fact]
    public async Task CreateRefund_WithPaymentOriginatingFromSameDevice_CallsBackend()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        await repository.UpsertCheckoutSessionAsync(
            CreateCheckoutSession("S01", "POS-01", paymentId: "payment-001"),
            CancellationToken.None);
        var backendService = new CapturingSquareTerminalBackendService();
        var controller = CreateController(repository, backendService);

        var result = await controller.CreateRefund(
            new SquareRefundRequest(
                "Production",
                "idem-refund-001",
                "payment-001",
                new SquareMoneyDto(1299, "AUD")),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, backendService.CreateRefundCalls);
    }

    [Fact]
    public async Task CreateRefund_WithPaymentFromAnotherStore_IsRejectedBeforeReachingSquare()
    {
        // Square access token 按环境全局共享，一旦漏掉归属校验，任一门店的终端都能
        // 对别店的付款打出真实退款。这里断言请求在到达 Square 之前就被拒绝。
        var repository = new InMemorySquareCheckoutSessionRepository();
        await repository.UpsertCheckoutSessionAsync(
            CreateCheckoutSession("S02", "POS-99", paymentId: "payment-foreign"),
            CancellationToken.None);
        var backendService = new CapturingSquareTerminalBackendService();
        var controller = CreateController(repository, backendService);

        var result = await controller.CreateRefund(
            new SquareRefundRequest(
                "Production",
                "idem-refund-002",
                "payment-foreign",
                new SquareMoneyDto(9999, "AUD")),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var apiResult = Assert.IsType<ApiResult<SquareRefundResponse>>(objectResult.Value);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
        Assert.Equal(0, backendService.CreateRefundCalls);
    }

    [Fact]
    public async Task CreateRefund_WithPaymentHavingNoTrustedOrigin_IsRejectedBeforeReachingSquare()
    {
        // 历史无来源行不能用当前收银员身份补写来源，必须一律拒绝续接。
        var repository = new InMemorySquareCheckoutSessionRepository();
        await repository.UpsertCheckoutSessionAsync(
            CreateCheckoutSession(null, null, paymentId: "payment-legacy"),
            CancellationToken.None);
        var backendService = new CapturingSquareTerminalBackendService();
        var controller = CreateController(repository, backendService);

        var result = await controller.CreateRefund(
            new SquareRefundRequest(
                "Production",
                "idem-refund-003",
                "payment-legacy",
                new SquareMoneyDto(500, "AUD")),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        Assert.Equal(0, backendService.CreateRefundCalls);
    }

    [Fact]
    public async Task GetCheckout_WhenCompletedBindsPaymentIdForSameDeviceContinuation()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        var existingSession = CreateCheckoutSession("S01", "POS-01");
        existingSession.PaymentIdsJson = "[\"payment-existing\"]";
        existingSession.RawCheckoutJson = "{\"trustedWebhook\":true}";
        existingSession.LastEventId = "event-existing";
        await repository.UpsertCheckoutSessionAsync(
            existingSession,
            CancellationToken.None);
        var backendService = new CapturingSquareTerminalBackendService
        {
            CheckoutResponse = new SquareCheckoutStatusResponse(
                "checkout-001",
                "Production",
                Status: "COMPLETED",
                PaymentIds: ["payment-001"]),
            PaymentResponse = new SquarePaymentStatusDto(
                "payment-001",
                Status: "COMPLETED",
                ApprovedMoney: new SquareMoneyDto(1299, "AUD"))
        };
        var controller = CreateController(repository, backendService);

        var checkoutResult = await controller.GetCheckout(
            "checkout-001",
            "Production",
            CancellationToken.None);
        var paymentResult = await controller.GetPayment(
            "payment-001",
            "Production",
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(checkoutResult.Result);
        Assert.IsType<OkObjectResult>(paymentResult.Result);
        var refreshed = Assert.Single(repository.Sessions);
        Assert.Equal("payment-001", refreshed.PaymentId);
        Assert.Contains("payment-existing", refreshed.PaymentIdsJson, StringComparison.Ordinal);
        Assert.Contains("payment-001", refreshed.PaymentIdsJson, StringComparison.Ordinal);
        Assert.Equal("{\"trustedWebhook\":true}", refreshed.RawCheckoutJson);
        Assert.Equal("event-existing", refreshed.LastEventId);
        Assert.Equal(1, backendService.GetPaymentCalls);
    }

    [Theory]
    [InlineData("checkout-other", "Production")]
    [InlineData("checkout-001", "Sandbox")]
    public async Task GetCheckout_WhenBackendIdentityDiffers_DoesNotBindForeignPayment(
        string responseCheckoutId,
        string responseEnvironment)
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        await repository.UpsertCheckoutSessionAsync(
            CreateCheckoutSession("S01", "POS-01"),
            CancellationToken.None);
        var backendService = new CapturingSquareTerminalBackendService
        {
            CheckoutResponse = new SquareCheckoutStatusResponse(
                responseCheckoutId,
                responseEnvironment,
                Status: "COMPLETED",
                PaymentIds: ["payment-foreign"])
        };
        var controller = CreateController(repository, backendService);

        var result = await controller.GetCheckout(
            "checkout-001",
            "Production",
            CancellationToken.None);

        var mismatch = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, mismatch.StatusCode);
        var payload = Assert.IsType<ApiResult<SquareCheckoutStatusResponse?>>(mismatch.Value);
        Assert.Equal("SQUARE_CHECKOUT_IDENTITY_MISMATCH", payload.ErrorCode);
        Assert.Null(Assert.Single(repository.Sessions).PaymentId);
        Assert.Equal(0, backendService.GetPaymentCalls);
    }

    [Fact]
    public async Task GetCheckout_WhenNewerPendingFollowsCompleted_PreservesTerminalObservation()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        var completedAt = new DateTimeOffset(2026, 7, 14, 0, 1, 0, TimeSpan.Zero);
        var completed = CreateCheckoutSession("S01", "POS-01");
        completed.Status = "COMPLETED";
        completed.Amount = 1299;
        completed.Currency = "AUD";
        completed.DeviceId = "device-trusted";
        completed.LocationId = "location-trusted";
        completed.UpdatedAt = completedAt;
        await repository.UpsertCheckoutSessionAsync(completed, CancellationToken.None);
        var controller = CreateController(
            repository,
            new CapturingSquareTerminalBackendService
            {
                CheckoutResponse = new SquareCheckoutStatusResponse(
                    "checkout-001",
                    "Production",
                    Status: "PENDING",
                    DeviceId: "device-stale",
                    LocationId: "location-stale",
                    AmountMoney: new SquareMoneyDto(9999, "USD"),
                    PaymentIds: ["payment-late"],
                    UpdatedAt: completedAt.AddMinutes(1))
            });

        var result = await controller.GetCheckout(
            "checkout-001",
            "Production",
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var preserved = Assert.Single(repository.Sessions);
        Assert.Equal("COMPLETED", preserved.Status);
        Assert.Equal(1299, preserved.Amount);
        Assert.Equal("AUD", preserved.Currency);
        Assert.Equal("device-trusted", preserved.DeviceId);
        Assert.Equal("location-trusted", preserved.LocationId);
        Assert.Equal(completedAt, preserved.UpdatedAt);
        Assert.Equal("payment-late", preserved.PaymentId);
    }

    [Theory]
    [InlineData("S02", "POS-01")]
    [InlineData("S01", "POS-02")]
    [InlineData(null, null)]
    public async Task GetCheckout_WithDifferentOrLegacyMissingOrigin_ReturnsForbidden(
        string? originStoreCode,
        string? originDeviceCode)
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        await repository.UpsertCheckoutSessionAsync(
            CreateCheckoutSession(originStoreCode, originDeviceCode),
            CancellationToken.None);
        var backendService = new CapturingSquareTerminalBackendService();
        var controller = CreateController(repository, backendService);

        var result = await controller.GetCheckout("checkout-001", "Production", CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(0, backendService.GetCheckoutCalls);
    }

    [Fact]
    public async Task CreateRefund_ReturnsBadRequestBeforeReadingTokenWhenIdempotencyKeyIsMissing()
    {
        var tokenRequested = false;
        var backendService = new CapturingSquareTerminalBackendService();
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 5, 26, 4, 0, 0, TimeSpan.Zero));

        await using var factory = new SquareApiFactory(
            new StubSquareTokenService(responseFactory: _ =>
            {
                tokenRequested = true;
                return Task.FromResult<SquareTokenResponse?>(expected);
            }),
            backendService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/square/refunds",
            new SquareRefundRequest(
                "Production",
                IdempotencyKey: "",
                PaymentId: "payment-001",
                AmountMoney: new SquareMoneyDto(500, "AUD")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareRefundResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_IDEMPOTENCY_KEY_REQUIRED", apiResult.ErrorCode);
        Assert.Equal("idempotencyKey is required.", apiResult.Message);
        Assert.False(tokenRequested);
        Assert.Equal(0, backendService.CreateRefundCalls);
    }

    [Fact]
    public async Task GetRefund_ReturnsCurrentRefundStatusThroughBackend()
    {
        var backendService = new CapturingSquareTerminalBackendService();
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 8, 17, 4, 0, 0, TimeSpan.Zero));
        await using var factory = new SquareApiFactory(
            new StubSquareTokenService(responseFactory: _ =>
                Task.FromResult<SquareTokenResponse?>(expected)),
            backendService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync(
            "/api/v1/square/refunds/refund-001?environment=Production");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareRefundResponse?>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult.Success);
        Assert.Equal("COMPLETED", apiResult.Data?.Status);
        Assert.Equal(1, backendService.GetRefundCalls);
    }

    [Fact]
    public async Task ReceiveWebhook_PassesRawBodyHeadersAndNotificationUrlToBackendService()
    {
        const string signature = "test-signature";
        const string squareEnvironment = "sandbox";
        const string rawBody = "{\"merchant_id\":\"merchant-123\",\"type\":\"terminal.checkout.updated\"}";
        var backendService = new CapturingSquareTerminalBackendService();

        await using var factory = new SquareApiFactory(backendService: backendService);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/square/webhooks");
        request.Headers.TryAddWithoutValidation("x-square-hmacsha256-signature", signature);
        request.Headers.TryAddWithoutValidation("square-environment", squareEnvironment);
        request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(backendService.LastWebhookRequest);
        Assert.Equal(signature, backendService.LastWebhookRequest!.SignatureHeader);
        Assert.Equal(squareEnvironment, backendService.LastWebhookRequest.SquareEnvironmentHeader);
        Assert.Equal(rawBody, backendService.LastWebhookRequest.RawBody);
        Assert.Equal("http://localhost/api/v1/square/webhooks", backendService.LastWebhookRequest.NotificationUrl);
    }

    [Fact]
    public async Task ReceiveWebhook_ReturnsStableForbiddenWhenSignatureIsInvalid()
    {
        const string rawBody = "{\"event_id\":\"event-001\",\"type\":\"terminal.checkout.updated\"}";

        await using var factory = new SquareApiFactory(
            backendService: new ThrowingSquareTerminalBackendService(
                () => new SquareTerminalBackendException(
                    "SQUARE_WEBHOOK_SIGNATURE_INVALID",
                    "Square webhook signature is invalid.",
                    HttpStatusCode.Forbidden)));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/square/webhooks");
        request.Headers.TryAddWithoutValidation("x-square-hmacsha256-signature", "invalid-signature");
        request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareWebhookAcceptedResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_WEBHOOK_SIGNATURE_INVALID", apiResult.ErrorCode);
        Assert.Equal("Square webhook signature is invalid.", apiResult.Message);
    }

    [Fact]
    public void Startup_FailsWhenSquareTokenSchemaInitializerThrows()
    {
        using var factory = new SquareApiFactory(
            schemaInitializer: new ThrowingSquareTokenSchemaInitializer(
                new InvalidOperationException("schema bootstrap failed without token values")));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("schema bootstrap failed", exception.Message);
        Assert.DoesNotContain(BackendToken, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_FailsWhenSquareWebhookSchemaInitializerThrows()
    {
        using var factory = new SquareApiFactory(
            webhookSchemaInitializer: new ThrowingSquareWebhookSchemaInitializer(
                new InvalidOperationException("square webhook schema bootstrap failed without secret leakage")),
            posmConnectionString: "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-test;Trusted_Connection=True;");

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("square webhook schema bootstrap failed", exception.Message);
        Assert.DoesNotContain(BackendToken, exception.Message, StringComparison.Ordinal);
    }

    private static void AssertHttpTemplate(string methodName, Type attributeType, string expectedTemplate)
    {
        var attribute = typeof(SquareController)
            .GetMethod(methodName)?
            .GetCustomAttributes(attributeType, inherit: false)
            .OfType<HttpMethodAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.Equal(expectedTemplate, attribute!.Template);
    }

    private static SquareController CreateController(
        ISquareCheckoutSessionRepository repository,
        ISquareTerminalBackendService backendService)
    {
        var token = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
        var controller = new SquareController(
            new StubSquareTokenService(_ => Task.FromResult<SquareTokenResponse?>(token)),
            backendService,
            new RecordingLogger<SquareController>(),
            repository);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
                    new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01")
                ],
                TestAuthHandler.SchemeName))
            }
        };
        return controller;
    }

    private static SquareCheckoutSessionRecord CreateCheckoutSession(
        string? originStoreCode,
        string? originDeviceCode,
        string? paymentId = null)
    {
        return new SquareCheckoutSessionRecord
        {
            Environment = "Production",
            CheckoutId = "checkout-001",
            Status = "PENDING",
            RawCheckoutJson = "{}",
            OriginStoreCode = originStoreCode,
            OriginDeviceCode = originDeviceCode,
            PaymentId = paymentId,
            UpdatedAt = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero)
        };
    }

    private sealed class SquareApiFactory(
        ISquareTokenService? squareTokenService = null,
        ISquareTokenSchemaInitializer? schemaInitializer = null,
        ISquareWebhookSchemaInitializer? webhookSchemaInitializer = null,
        ISquareTerminalBackendService? backendService = null,
        ISquareCheckoutSessionRepository? checkoutSessionRepository = null,
        string? posmConnectionString = null,
        string? squareApiVersion = null,
        RecordingLogger<SquareController>? squareLogger = null,
        bool registerBackendService = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                // Square 控制器旧行为测试运行在发布 Audit 阶段，不要求构造真实收银员数据库服务。
                var settings = new Dictionary<string, string?>
                {
                    ["CashierAuthorization:Mode"] = "Audit"
                };
                if (!string.IsNullOrWhiteSpace(posmConnectionString))
                {
                    settings["ConnectionStrings:MainConnection"] = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-main-test;Trusted_Connection=True;";
                    settings["ConnectionStrings:PosmConnection"] = posmConnectionString;
                }

                if (squareApiVersion is not null)
                {
                    settings["Square:ApiVersion"] = squareApiVersion;
                }

                configurationBuilder.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                });

                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName,
                        _ => { });

                services.RemoveAll<IPosIpadAppReviewAuthorizationBoundary>();
                services.AddSingleton<IPosIpadAppReviewAuthorizationBoundary>(
                    new NonReviewDeviceAuthorizationBoundary());

                services.RemoveAll<ISquareTokenService>();
                services.AddSingleton(squareTokenService ?? new StubSquareTokenService());

                services.RemoveAll<IStoreSchemaInitializer>();
                services.AddSingleton<IStoreSchemaInitializer>(new NoOpStoreSchemaInitializer());

                services.RemoveAll<IAttendanceQrKeySchemaInitializer>();
                services.AddSingleton<IAttendanceQrKeySchemaInitializer>(new TestNoOpAttendanceQrKeySchemaInitializer());

                services.RemoveAll<IDeviceRuntimeStatusSchemaInitializer>();
                services.AddSingleton<IDeviceRuntimeStatusSchemaInitializer>(new NoOpDeviceRuntimeStatusSchemaInitializer());

                // 此测试宿主只验证 Square 启动门禁，不应让共享挂单 schema 访问本机业务库。
                services.RemoveAll<ISharedHeldOrderSchemaInitializer>();
                services.AddSingleton<ISharedHeldOrderSchemaInitializer>(new NoOpSharedHeldOrderSchemaInitializer());

                services.RemoveAll<IOperationAuditSchemaInitializer>();
                services.AddSingleton<IOperationAuditSchemaInitializer>(new TestNoOpOperationAuditSchemaInitializer());

                services.RemoveAll<IOrderSyncSchemaInitializer>();
                services.AddSingleton<IOrderSyncSchemaInitializer>(new TestNoOpOrderSyncSchemaInitializer());

                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton(schemaInitializer ?? new NoOpSquareTokenSchemaInitializer());

                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton<ILinklyCloudCredentialSchemaInitializer>(
                    new NoOpLinklyCloudCredentialSchemaInitializer());

                services.RemoveAll<ILinklyCloudBackendAsyncSchemaInitializer>();
                services.AddSingleton<ILinklyCloudBackendAsyncSchemaInitializer>(
                    new NoOpLinklyCloudBackendAsyncSchemaInitializer());

                services.RemoveAll<ILinklySettlementSchemaInitializer>();
                services.AddSingleton<ILinklySettlementSchemaInitializer>(
                    new TestNoOpLinklySettlementSchemaInitializer());

                services.RemoveAll<IDailyCloseSchemaInitializer>();
                services.AddSingleton<IDailyCloseSchemaInitializer>(
                    new TestNoOpDailyCloseSchemaInitializer());

                services.RemoveAll<IAdvertisementSchemaInitializer>();
                services.AddSingleton<IAdvertisementSchemaInitializer>(new NoOpAdvertisementSchemaInitializer());

                services.RemoveAll<ISquareWebhookSchemaInitializer>();
                services.AddSingleton(webhookSchemaInitializer ?? new NoOpSquareWebhookSchemaInitializer());

                services.RemoveAll<IInstallmentRepaymentClaimSchemaInitializer>();
                services.AddSingleton<IInstallmentRepaymentClaimSchemaInitializer>(
                    new TestNoOpInstallmentRepaymentClaimSchemaInitializer());
                services.RemoveAll<IInstallmentCancelClaimSchemaInitializer>();
                services.AddSingleton<IInstallmentCancelClaimSchemaInitializer>(
                    new TestNoOpInstallmentCancelClaimSchemaInitializer());

                services.RemoveAll<ISquareTerminalBackendService>();
                if (registerBackendService)
                {
                    services.AddSingleton(backendService ?? new CapturingSquareTerminalBackendService());
                }

                services.RemoveAll<ISquareCheckoutSessionRepository>();
                services.AddSingleton(checkoutSessionRepository ?? new InMemorySquareCheckoutSessionRepository());

                if (squareLogger is not null)
                {
                    services.AddSingleton<ILogger<SquareController>>(squareLogger);
                }
            });
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }
    }

    private sealed class NoOpSquareTokenSchemaInitializer : ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSquareTokenSchemaInitializer(Exception exception) : ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class NoOpSquareWebhookSchemaInitializer : ISquareWebhookSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSquareWebhookSchemaInitializer(Exception exception) : ISquareWebhookSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class NoOpStoreSchemaInitializer : IStoreSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpDeviceRuntimeStatusSchemaInitializer : IDeviceRuntimeStatusSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSharedHeldOrderSchemaInitializer : ISharedHeldOrderSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpLinklyCloudCredentialSchemaInitializer : ILinklyCloudCredentialSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpLinklyCloudBackendAsyncSchemaInitializer : ILinklyCloudBackendAsyncSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpAdvertisementSchemaInitializer : IAdvertisementSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "SquareTestAuth";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(header) || !string.Equals(header, "Test", StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01"),
                new Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
                new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class StubSquareTokenService(
        Func<string, Task<SquareTokenResponse?>>? responseFactory = null,
        Func<string, Exception>? exceptionFactory = null) : ISquareTokenService
    {
        public Task<SquareTokenResponse?> GetActiveTokenAsync(
            string environment,
            CancellationToken cancellationToken)
        {
            if (exceptionFactory is not null)
            {
                throw exceptionFactory(environment);
            }

            if (responseFactory is not null)
            {
                return responseFactory(environment);
            }

            return Task.FromResult<SquareTokenResponse?>(null);
        }
    }

    private sealed class ThrowingSquareTerminalBackendService(Func<Exception> exceptionFactory) : ISquareTerminalBackendService
    {
        public Task<IReadOnlyList<SquareLocationDto>> GetLocationsAsync(string environment, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<IReadOnlyList<SquareDeviceDto>> GetDevicesAsync(string environment, string locationId, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<IReadOnlyList<SquareDeviceCodeDto>> GetDeviceCodesAsync(string environment, string locationId, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareDeviceCodeDto> CreateDeviceCodeAsync(SquareCreateDeviceCodeRequest request, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareDeviceCodeDto?> GetDeviceCodeAsync(string environment, string deviceCodeId, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareCheckoutStatusResponse> CreateCheckoutAsync(SquareCreateCheckoutRequest request, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareCheckoutStatusResponse?> GetCheckoutAsync(string environment, string checkoutId, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquarePaymentStatusDto?> GetPaymentAsync(string environment, string paymentId, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareCheckoutStatusResponse> CancelCheckoutAsync(string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareCheckoutStatusResponse> DismissCheckoutAsync(string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareRefundResponse> CreateRefundAsync(SquareRefundRequest request, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareRefundResponse?> GetRefundAsync(string environment, string refundId, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }

        public Task<SquareWebhookAcceptedResponse> AcceptWebhookAsync(SquareWebhookRequest request, CancellationToken cancellationToken)
        {
            throw exceptionFactory();
        }
    }

    private sealed class CapturingSquareTerminalBackendService : ISquareTerminalBackendService
    {
        public SquareWebhookRequest? LastWebhookRequest { get; private set; }

        public SquareCheckoutStatusResponse? CheckoutResponse { get; init; }

        public SquarePaymentStatusDto? PaymentResponse { get; init; }

        public int CreateDeviceCodeCalls { get; private set; }

        public int CreateCheckoutCalls { get; private set; }

        public int GetCheckoutCalls { get; private set; }

        public int GetPaymentCalls { get; private set; }

        public int CreateRefundCalls { get; private set; }

        public int GetRefundCalls { get; private set; }

        public Task<IReadOnlyList<SquareLocationDto>> GetLocationsAsync(string environment, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<SquareDeviceDto>> GetDevicesAsync(string environment, string locationId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<SquareDeviceCodeDto>> GetDeviceCodesAsync(string environment, string locationId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<SquareDeviceCodeDto> CreateDeviceCodeAsync(SquareCreateDeviceCodeRequest request, CancellationToken cancellationToken)
        {
            CreateDeviceCodeCalls++;
            return Task.FromResult(new SquareDeviceCodeDto(
                "device-code-001",
                Code: "ABCDEF",
                Status: "UNPAIRED",
                LocationId: request.LocationId,
                Name: request.Name));
        }

        public Task<SquareDeviceCodeDto?> GetDeviceCodeAsync(string environment, string deviceCodeId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<SquareCheckoutStatusResponse> CreateCheckoutAsync(SquareCreateCheckoutRequest request, CancellationToken cancellationToken)
        {
            CreateCheckoutCalls++;
            return Task.FromResult(new SquareCheckoutStatusResponse(
                "checkout-001",
                request.Environment,
                Status: "PENDING",
                DeviceId: request.DeviceId,
                LocationId: request.LocationId,
                AmountMoney: request.AmountMoney));
        }

        public Task<SquareCheckoutStatusResponse?> GetCheckoutAsync(string environment, string checkoutId, CancellationToken cancellationToken)
        {
            GetCheckoutCalls++;
            return Task.FromResult<SquareCheckoutStatusResponse?>(CheckoutResponse ?? new SquareCheckoutStatusResponse(
                checkoutId,
                environment,
                Status: "PENDING"));
        }

        public Task<SquarePaymentStatusDto?> GetPaymentAsync(string environment, string paymentId, CancellationToken cancellationToken)
        {
            GetPaymentCalls++;
            return Task.FromResult(PaymentResponse);
        }

        public Task<SquareCheckoutStatusResponse> CancelCheckoutAsync(string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<SquareCheckoutStatusResponse> DismissCheckoutAsync(string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<SquareRefundResponse> CreateRefundAsync(SquareRefundRequest request, CancellationToken cancellationToken)
        {
            CreateRefundCalls++;
            return Task.FromResult(new SquareRefundResponse(
                "refund-001",
                request.Environment,
                Status: "PENDING",
                PaymentId: request.PaymentId,
                AmountMoney: request.AmountMoney));
        }

        public Task<SquareRefundResponse?> GetRefundAsync(string environment, string refundId, CancellationToken cancellationToken)
        {
            GetRefundCalls++;
            return Task.FromResult<SquareRefundResponse?>(new SquareRefundResponse(
                refundId,
                environment,
                Status: "COMPLETED",
                PaymentId: "payment-001",
                AmountMoney: new SquareMoneyDto(1000, "AUD")));
        }

        public Task<SquareWebhookAcceptedResponse> AcceptWebhookAsync(SquareWebhookRequest request, CancellationToken cancellationToken)
        {
            LastWebhookRequest = request;
            return Task.FromResult(new SquareWebhookAcceptedResponse("accepted", Message: "captured"));
        }
    }

    private sealed class InMemorySquareCheckoutSessionRepository : ISquareCheckoutSessionRepository
    {
        private readonly Dictionary<string, SquareCheckoutSessionRecord> sessions =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<SquareCheckoutSessionRecord> Sessions => sessions.Values;

        public Task<SquareCheckoutSessionRecord?> BindCheckoutOriginAsync(
            string environment,
            string checkoutId,
            string originStoreCode,
            string originDeviceCode,
            CancellationToken cancellationToken)
        {
            sessions.TryGetValue($"{environment}::{checkoutId}", out var session);
            if (session is not null)
            {
                session.OriginStoreCode ??= originStoreCode;
                session.OriginDeviceCode ??= originDeviceCode;
            }

            return Task.FromResult(session);
        }

        public Task<bool> TryAddWebhookEventAsync(
            SquareWebhookEventRecord webhookEvent,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task UpsertCheckoutSessionAsync(
            SquareCheckoutSessionRecord session,
            CancellationToken cancellationToken)
        {
            var key = $"{session.Environment}::{session.CheckoutId}";
            if (sessions.TryGetValue(key, out var existing))
            {
                // 模拟生产 MERGE：webhook 空来源不能覆盖创建时写入的设备边界。
                session.OriginStoreCode = existing.OriginStoreCode ?? session.OriginStoreCode;
                session.OriginDeviceCode = existing.OriginDeviceCode ?? session.OriginDeviceCode;
            }

            sessions[key] = session;
            return Task.CompletedTask;
        }

        public Task<bool> RefreshCheckoutSessionFromLookupAsync(
            SquareCheckoutSessionRecord session,
            string originStoreCode,
            string originDeviceCode,
            CancellationToken cancellationToken)
        {
            var key = $"{session.Environment}::{session.CheckoutId}";
            if (!sessions.TryGetValue(key, out var existing) ||
                !string.Equals(existing.OriginStoreCode, originStoreCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.OriginDeviceCode, originDeviceCode, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            var paymentIds = ReadPaymentIds(existing.PaymentIdsJson)
                .Concat(ReadPaymentIds(session.PaymentIdsJson))
                .Append(session.PaymentId)
                .Where(paymentId => !string.IsNullOrWhiteSpace(paymentId))
                .Select(paymentId => paymentId!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (session.UpdatedAt >= existing.UpdatedAt &&
                (!IsTerminal(existing.Status) || IsTerminal(session.Status)))
            {
                existing.Status = string.IsNullOrWhiteSpace(session.Status)
                    ? existing.Status
                    : session.Status;
                existing.Amount = session.Amount ?? existing.Amount;
                existing.Currency = session.Currency ?? existing.Currency;
                existing.DeviceId = session.DeviceId ?? existing.DeviceId;
                existing.LocationId = session.LocationId ?? existing.LocationId;
                existing.UpdatedAt = session.UpdatedAt;
            }
            existing.PaymentId ??= session.PaymentId;
            existing.PaymentIdsJson = paymentIds.Length == 0
                ? existing.PaymentIdsJson
                : JsonSerializer.Serialize(paymentIds);
            return Task.FromResult(true);
        }

        private static IEnumerable<string?> ReadPaymentIds(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return [];
            try
            {
                return JsonSerializer.Deserialize<string?[]>(payload) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static bool IsTerminal(string? status)
        {
            return status is "COMPLETED" or "CANCELED" or "CANCELLED" or "FAILED";
        }

        public Task<SquareCheckoutSessionRecord?> GetCheckoutSessionAsync(
            string environment,
            string checkoutId,
            CancellationToken cancellationToken)
        {
            sessions.TryGetValue($"{environment}::{checkoutId}", out var session);
            return Task.FromResult(session);
        }

        public Task<SquareCheckoutSessionRecord?> GetCheckoutSessionByPaymentIdAsync(
            string environment,
            string paymentId,
            CancellationToken cancellationToken)
        {
            var session = sessions.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(candidate.PaymentId, paymentId, StringComparison.OrdinalIgnoreCase) ||
                 candidate.PaymentIdsJson?.Contains(paymentId, StringComparison.OrdinalIgnoreCase) == true));
            return Task.FromResult(session);
        }
    }
}
