using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Hbpos.Api;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class LinklyControllerTests
{
    [Fact]
    public void LinklyCloudCredentialEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("cloud-credential", typeof(LinklyController)
            .GetMethod(nameof(LinklyController.GetCloudCredential))?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template);
        Assert.NotNull(typeof(LinklyController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public void LinklyCloudCredentialUpsertEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("cloud-credential", typeof(LinklyController)
            .GetMethod(nameof(LinklyController.UpsertCloudCredential))?
            .GetCustomAttributes(typeof(HttpPutAttribute), inherit: false)
            .Cast<HttpPutAttribute>()
            .Single()
            .Template);
        Assert.NotNull(typeof(LinklyController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public void LinklyCloudBackendSettlementEndpoints_KeepExpectedRoutesAndAuthorization()
    {
        Assert.Equal("cloud-backend/settlements", GetRoute<HttpPostAttribute>(nameof(LinklyController.StartCloudBackendSettlement)));
        Assert.Equal("cloud-backend/settlements/resumable", GetRoute<HttpGetAttribute>(nameof(LinklyController.GetResumableCloudBackendSettlement)));
        Assert.Equal("cloud-backend/settlements/{sessionId}/status", GetRoute<HttpGetAttribute>(nameof(LinklyController.GetCloudBackendSettlementStatus)));
        Assert.Equal("cloud-backend/settlements/{sessionId}/acknowledge", GetRoute<HttpPostAttribute>(nameof(LinklyController.AcknowledgeCloudBackendSettlement)));
        Assert.Equal("cloud-backend/settlements/{sessionId}/receipt/printed", GetRoute<HttpPostAttribute>(nameof(LinklyController.MarkCloudBackendSettlementReceiptPrinted)));

        foreach (var action in new[]
                 {
                     nameof(LinklyController.StartCloudBackendSettlement),
                     nameof(LinklyController.GetResumableCloudBackendSettlement),
                     nameof(LinklyController.GetCloudBackendSettlementStatus),
                     nameof(LinklyController.AcknowledgeCloudBackendSettlement)
                 })
        {
            Assert.Equal(
                CashierAuthorizationPolicies.DailyCloseSave,
                typeof(LinklyController).GetMethod(action)?
                    .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                    .Cast<AuthorizeAttribute>()
                    .Single()
                    .Policy);
        }

        Assert.Equal(
            CashierAuthorizationPolicies.DailyClosePrint,
            typeof(LinklyController).GetMethod(nameof(LinklyController.MarkCloudBackendSettlementReceiptPrinted))?
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
    }

    [Fact]
    public async Task GetCloudCredential_RequiresAuthentication()
    {
        await using var factory = new LinklyApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCloudCredential_UsesAuthenticatedStoreCodeOnly()
    {
        string? requestedStoreCode = null;
        string? requestedEnvironment = null;
        var expected = new LinklyCloudCredentialResponse(
            "S01",
            "Sandbox",
            "merchant-user",
            "merchant-password",
            new DateTimeOffset(2026, 5, 28, 4, 0, 0, TimeSpan.Zero));

        await using var factory = new LinklyApiFactory(new StubLinklyCloudCredentialService(
            responseFactory: (storeCode, environment) =>
            {
                requestedStoreCode = storeCode;
                requestedEnvironment = environment;
                return Task.FromResult<LinklyCloudCredentialResponse?>(expected);
            }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential?storeCode=S99&environment=sandbox");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudCredentialResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", requestedStoreCode);
        Assert.Equal("Sandbox", requestedEnvironment);
        Assert.Equal(expected.StoreCode, apiResult.Data?.StoreCode);
        Assert.Equal(expected.Environment, apiResult.Data?.Environment);
        Assert.Equal(expected.Username, apiResult.Data?.Username);
        Assert.Equal(expected.Password, apiResult.Data?.Password);
    }

    [Fact]
    public async Task UpsertCloudCredential_UsesAuthenticatedStoreCodeOnlyAndDoesNotExposePassword()
    {
        string? requestedStoreCode = null;
        LinklyCloudCredentialUpsertRequest? requestedRequest = null;
        string? requestedUpdatedBy = null;
        await using var factory = new LinklyApiFactory(new StubLinklyCloudCredentialService(
            upsertFactory: (storeCode, request, updatedBy) =>
            {
                requestedStoreCode = storeCode;
                requestedRequest = request;
                requestedUpdatedBy = updatedBy;
                return Task.FromResult(new LinklyCloudCredentialUpsertResponse(
                    storeCode,
                    "Sandbox",
                    "merchant-user",
                    true,
                    new DateTimeOffset(2026, 6, 2, 1, 0, 0, TimeSpan.Zero)));
            }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PutAsJsonAsync(
            "/api/v1/linkly/cloud-credential?storeCode=S99",
            new
            {
                Environment = "Sandbox",
                Username = "merchant-user",
                Password = "merchant-password",
                StoreCode = "S99"
            });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = JsonSerializer.Deserialize<ApiResult<LinklyCloudCredentialUpsertResponse>>(
            body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", requestedStoreCode);
        Assert.Equal("Sandbox", requestedRequest?.Environment);
        Assert.Equal("device:POS-01", requestedUpdatedBy);
        Assert.True(apiResult.Data?.HasPassword);
        Assert.Equal("S01", apiResult.Data?.StoreCode);
        Assert.DoesNotContain("merchant-password", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartCloudBackendTransaction_UsesAuthenticatedDeviceClaimsOnly()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var request = new
        {
            Environment = "Sandbox",
            RestBaseUrl = "https://attacker.example/v1/",
            AccessToken = "attacker-token",
            StoreCode = "S99",
            DeviceCode = "POS-99",
            TxnType = "P",
            AmtPurchase = 1000,
            TxnRef = "CLIENT-TXN",
            PurchaseAnalysisData = (IReadOnlyDictionary<string, string>?)null
        };

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions?storeCode=S99&deviceCode=POS-99",
            request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastStoreCode);
        Assert.Equal("POS-01", backendService.LastDeviceCode);
        Assert.NotNull(backendService.LastStartRequest);
        Assert.Equal("Sandbox", backendService.LastStartRequest!.Environment);
        Assert.Equal("P", backendService.LastStartRequest.TxnType);
        Assert.Equal(1000, backendService.LastStartRequest.AmtPurchase);
        Assert.Equal("S01", apiResult.Data?.StoreCode);
        Assert.Equal("POS-01", apiResult.Data?.DeviceCode);
        Assert.Equal("SERVER-TXN", apiResult.Data?.TxnRef);
    }

    [Fact]
    public void CloudBackendAsyncRequests_DoNotExposeClientCredentialsOrDeviceScopeFields()
    {
        var forbiddenFields = new[] { "AccessToken", "RestBaseUrl", "TxnRef", "StoreCode", "DeviceCode" };

        Assert.DoesNotContain(
            typeof(LinklyCloudBackendTransactionRequest).GetProperties().Select(property => property.Name),
            forbiddenFields.Contains);
        Assert.DoesNotContain(
            typeof(LinklyCloudBackendRecoverRequest).GetProperties().Select(property => property.Name),
            forbiddenFields.Contains);
        Assert.DoesNotContain(
            typeof(LinklyCloudBackendSendKeyRequest).GetProperties().Select(property => property.Name),
            forbiddenFields.Contains);
        Assert.DoesNotContain(
            typeof(LinklyCloudBackendTerminalCredentialUpsertRequest).GetProperties().Select(property => property.Name),
            forbiddenFields.Contains);
    }

    [Fact]
    public void CloudBackendNotificationEndpoint_UsesPublicRouteWithoutDeviceScope()
    {
        Assert.Equal("cloud-notifications/{environment}/{sessionId}/{type}", typeof(LinklyController)
            .GetMethod(nameof(LinklyController.ReceiveCloudBackendNotification))?
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single()
            .Template);
    }

    [Fact]
    public void CloudBackendHealthEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("cloud-backend/health", typeof(LinklyController)
            .GetMethod(nameof(LinklyController.GetCloudBackendHealth))?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template);
    }

    [Fact]
    public void CloudBackendStatusTestEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("cloud-backend/status-test", typeof(LinklyController)
            .GetMethod(nameof(LinklyController.RunCloudBackendStatusTest))?
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single()
            .Template);
    }

    [Fact]
    public void CloudBackendLogonTestEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("cloud-backend/logon-test", typeof(LinklyController)
            .GetMethod(nameof(LinklyController.RunCloudBackendLogonTest))?
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single()
            .Template);
    }

    [Fact]
    public void CloudBackendTerminalCredentialEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("cloud-backend/terminal", typeof(LinklyController)
            .GetMethod(nameof(LinklyController.UpsertCloudBackendTerminalCredential))?
            .GetCustomAttributes(typeof(HttpPutAttribute), inherit: false)
            .Cast<HttpPutAttribute>()
            .Single()
            .Template);
    }

    [Fact]
    public async Task StartCloudBackendTransaction_ReturnsBadRequestWhenBackendCredentialIsMissing()
    {
        await using var factory = new LinklyApiFactory(
            linklyCloudBackendAsyncService: new CapturingLinklyCloudBackendAsyncService(
                new LinklyCloudBackendValidationException(
                    "Linkly Cloud credential is not configured for this store and environment.")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions",
            new
            {
                Environment = "Sandbox",
                TxnType = "P",
                AmtPurchase = 1000
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_BACKEND_REQUEST_INVALID", apiResult.ErrorCode);
        Assert.Equal(
            "Linkly Cloud credential is not configured for this store and environment.",
            apiResult.Message);
    }

    [Theory]
    [InlineData(true, "LINKLY_CLOUD_TERMINAL_CREDENTIAL_REENTRY_REQUIRED", "Linkly Cloud terminal credentials must be re-entered in the management portal.")]
    [InlineData(false, "LINKLY_CLOUD_TERMINAL_CREDENTIAL_UNAVAILABLE", "Linkly Cloud terminal credentials are unavailable. Re-enter them in the management portal.")]
    public async Task StartCloudBackendTransaction_returns_fixed_safe_credential_failure(
        bool reentryRequired,
        string expectedCode,
        string expectedMessage)
    {
        await using var factory = new LinklyApiFactory(
            linklyCloudBackendAsyncService: new CapturingLinklyCloudBackendAsyncService(
                reentryRequired
                    ? new LinklyCloudTerminalCredentialReentryRequiredException()
                    : new LinklyCloudTerminalCredentialUnavailableException()));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions",
            new
            {
                Environment = "Sandbox",
                TxnType = "P",
                AmtPurchase = 1000,
                TerminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                SelectionRevision = 1
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content
            .ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(envelope);
        Assert.Equal(expectedCode, envelope!.ErrorCode);
        Assert.Equal(expectedMessage, envelope.Message);
    }

    [Fact]
    public async Task GetCloudCredential_ReturnsBadRequestForInvalidEnvironment()
    {
        await using var factory = new LinklyApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential?environment=staging");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudCredentialResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_CREDENTIAL_ENVIRONMENT_INVALID", apiResult.ErrorCode);
        Assert.Equal("environment must be Production or Sandbox", apiResult.Message);
    }

    [Fact]
    public async Task GetCloudBackendActiveTransaction_UsesAuthenticatedDeviceClaimsOnly()
    {
        var expected = CreateBackendResponse("active-session", "Pending");
        var backendService = new CapturingLinklyCloudBackendAsyncService(activeResponse: expected);
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync(
            "/api/v1/linkly/cloud-backend/transactions/active?environment=sandbox&storeCode=S99&deviceCode=POS-99");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastActiveStoreCode);
        Assert.Equal("POS-01", backendService.LastActiveDeviceCode);
        Assert.Equal("sandbox", backendService.LastActiveEnvironment);
        Assert.Equal("active-session", apiResult.Data?.SessionId);
    }

    [Fact]
    public async Task GetCloudBackendActiveTransaction_ReturnsNotFoundWhenNoActiveSession()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService(activeResponse: null);
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-backend/transactions/active?environment=Sandbox");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetCloudBackendResumableTransaction_UsesAuthenticatedDeviceClaimsOnly()
    {
        var expected = CreateBackendResponse("resumable-session", "Completed");
        var backendService = new CapturingLinklyCloudBackendAsyncService(resumableResponse: expected);
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync(
            "/api/v1/linkly/cloud-backend/transactions/resumable?environment=sandbox&storeCode=S99&deviceCode=POS-99");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastResumableStoreCode);
        Assert.Equal("POS-01", backendService.LastResumableDeviceCode);
        Assert.Equal("sandbox", backendService.LastResumableEnvironment);
        Assert.Equal("resumable-session", apiResult.Data?.SessionId);
    }

    [Fact]
    public async Task GetCloudBackendResumableTransaction_ReturnsNotFoundWhenNoSession()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService(resumableResponse: null);
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-backend/transactions/resumable?environment=Sandbox");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetCloudBackendHealth_UsesAuthenticatedDeviceClaimsAndReturnsConfigurationReadiness()
    {
        var expected = new LinklyCloudBackendHealthResponse(
            "Sandbox",
            "S01",
            "POS-01",
            true,
            "https://public.example/callback/",
            [
                new LinklyCloudBackendHealthCheckDto("STORE_CREDENTIAL", true, "ok"),
                new LinklyCloudBackendHealthCheckDto("TERMINAL_SECRET", true, "ok"),
                new LinklyCloudBackendHealthCheckDto("TERMINAL_POS_ID", true, "ok"),
                new LinklyCloudBackendHealthCheckDto("NOTIFICATION_BEARER", true, "ok"),
                new LinklyCloudBackendHealthCheckDto("PUBLIC_CALLBACK_URL", true, "ok")
            ]);
        var backendService = new CapturingLinklyCloudBackendAsyncService(healthResponse: expected);
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync(
            "/api/v1/linkly/cloud-backend/health?environment=sandbox&storeCode=S99&deviceCode=POS-99");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendHealthResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastHealthStoreCode);
        Assert.Equal("POS-01", backendService.LastHealthDeviceCode);
        Assert.Equal("sandbox", backendService.LastHealthEnvironment);
        Assert.True(apiResult.Data?.IsReady);
        Assert.Contains(apiResult.Data!.Checks, check => check.Code == "PUBLIC_CALLBACK_URL" && check.IsReady);
    }

    [Fact]
    public async Task RunCloudBackendStatusTest_UsesAuthenticatedDeviceClaimsOnly()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsync(
            "/api/v1/linkly/cloud-backend/status-test?environment=sandbox",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendStatusTestResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastStatusTestStoreCode);
        Assert.Equal("POS-01", backendService.LastStatusTestDeviceCode);
        Assert.Equal("sandbox", backendService.LastStatusTestEnvironment);
        Assert.True(apiResult.Data?.Succeeded);
        Assert.Equal("S01", apiResult.Data?.StoreCode);
        Assert.Equal("POS-01", apiResult.Data?.DeviceCode);
        Assert.Equal("status-session-1", apiResult.Data?.TransactionReference);
    }

    [Fact]
    public async Task RunCloudBackendLogonTest_UsesAuthenticatedDeviceClaimsOnly()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsync(
            "/api/v1/linkly/cloud-backend/logon-test?environment=sandbox",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendLogonTestResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastLogonTestStoreCode);
        Assert.Equal("POS-01", backendService.LastLogonTestDeviceCode);
        Assert.Equal("sandbox", backendService.LastLogonTestEnvironment);
        Assert.True(apiResult.Data?.Succeeded);
        Assert.Equal("S01", apiResult.Data?.StoreCode);
        Assert.Equal("POS-01", apiResult.Data?.DeviceCode);
        Assert.Equal("logon-session-1", apiResult.Data?.TransactionReference);
    }

    [Theory]
    [InlineData("status-test")]
    [InlineData("logon-test")]
    public async Task CloudBackendTerminalTest_MapsPhysicalTerminalFenceToStableConflict(string route)
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService(
            terminalTestException: new LinklyCloudBackendActiveTransactionException("active-session"));
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsync(
            $"/api/v1/linkly/cloud-backend/{route}?environment=sandbox",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<object>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION", apiResult.ErrorCode);
    }

    [Fact]
    public async Task StartCloudBackendTransaction_maps_rejected_terminal_secret_to_repair_required_conflict()
    {
        // M9：token 端点 401/403 说明 secret 已失效，必须是专用错误码（needs-repair），而不是通用 400。
        await using var factory = new LinklyApiFactory(
            linklyCloudBackendAsyncService: new CapturingLinklyCloudBackendAsyncService(
                new LinklyCloudTerminalRepairRequiredException(HttpStatusCode.Unauthorized)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions",
            new { Environment = "Sandbox", TxnType = "P", AmtPurchase = 1000 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.Equal("LINKLY_CLOUD_TERMINAL_REPAIR_REQUIRED", envelope!.ErrorCode);
    }

    [Fact]
    public async Task StartCloudBackendTransaction_maps_token_endpoint_outage_to_retryable_service_unavailable()
    {
        // M1：认证端 429/5xx/网络错误不是请求格式问题，不能返回 400/500。
        await using var factory = new LinklyApiFactory(
            linklyCloudBackendAsyncService: new CapturingLinklyCloudBackendAsyncService(
                new LinklyCloudBackendTokenUnavailableException(HttpStatusCode.TooManyRequests, "throttled")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions",
            new { Environment = "Sandbox", TxnType = "P", AmtPurchase = 1000 });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Equal("5", Assert.Single(retryAfter));
        var envelope = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.Equal("LINKLY_CLOUD_BACKEND_TOKEN_UNAVAILABLE", envelope!.ErrorCode);
    }

    [Fact]
    public async Task StartCloudBackendTransaction_reports_probe_lease_with_dedicated_code_and_remaining_seconds()
    {
        // M10：被 Status/Logon 检测的短租约挡住时，不能报成“有未完成交易”。
        await using var factory = new LinklyApiFactory(
            linklyCloudBackendAsyncService: new CapturingLinklyCloudBackendAsyncService(
                new LinklyCloudTerminalProbingException(37)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions",
            new { Environment = "Sandbox", TxnType = "P", AmtPurchase = 1000 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("37", Assert.Single(response.Headers.GetValues("Retry-After")));
        var envelope = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.Equal("LINKLY_CLOUD_TERMINAL_PROBING", envelope!.ErrorCode);
        Assert.Contains("37", envelope.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status-test")]
    [InlineData("logon-test")]
    public async Task CloudBackendTerminalTest_reports_probe_lease_with_dedicated_code_and_remaining_seconds(string route)
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService(
            terminalTestException: new LinklyCloudTerminalProbingException(12));
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsync($"/api/v1/linkly/cloud-backend/{route}?environment=sandbox", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("12", Assert.Single(response.Headers.GetValues("Retry-After")));
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<object>>();
        Assert.Equal("LINKLY_CLOUD_TERMINAL_PROBING", apiResult!.ErrorCode);
    }

    [Fact]
    public async Task GetCloudBackendTransactionStatus_maps_rejected_terminal_secret_to_repair_required_conflict()
    {
        await using var factory = new LinklyApiFactory(
            linklyCloudBackendAsyncService: new CapturingLinklyCloudBackendAsyncService(
                statusException: new LinklyCloudTerminalRepairRequiredException(HttpStatusCode.Forbidden)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync(
            "/api/v1/linkly/cloud-backend/transactions/session-1/status?environment=Sandbox");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.Equal("LINKLY_CLOUD_TERMINAL_REPAIR_REQUIRED", envelope!.ErrorCode);
    }

    [Theory]
    [InlineData(404, HttpStatusCode.BadRequest)]
    [InlineData(409, HttpStatusCode.BadRequest)]
    [InlineData(422, HttpStatusCode.BadRequest)]
    [InlineData(400, HttpStatusCode.BadRequest)]
    [InlineData(401, HttpStatusCode.OK)]
    [InlineData(408, HttpStatusCode.OK)]
    [InlineData(429, HttpStatusCode.OK)]
    [InlineData(200, HttpStatusCode.OK)]
    public async Task SendCloudBackendKey_keeps_the_400_contract_for_every_rejected_key_press(
        int linklyStatus,
        HttpStatusCode expected)
    {
        // M2：sendkey 被 Linkly 以 404/409/422 拒绝时会话保持 Pending，对客户端仍沿用既有的 400 约定。
        var sendKeyResponse = CreateBackendResponse("session-key", "Pending") with { LastHttpStatus = linklyStatus };
        await using var factory = new LinklyApiFactory(
            linklyCloudBackendAsyncService: new CapturingLinklyCloudBackendAsyncService(sendKeyResponse: sendKeyResponse));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions/session-key/sendkey",
            new LinklyCloudBackendSendKeyRequest("Sandbox", "0", null));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task UpsertCloudBackendTerminalCredential_UsesAuthenticatedDeviceClaimsOnlyAndDoesNotExposeSecret()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PutAsJsonAsync(
            "/api/v1/linkly/cloud-backend/terminal?storeCode=S99&deviceCode=POS-99",
            new
            {
                Environment = "Sandbox",
                Secret = "secret-pos-01",
                PosId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                StoreCode = "S99",
                DeviceCode = "POS-99"
            });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendTerminalCredentialResponse>>(
            body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastTerminalUpsertStoreCode);
        Assert.Equal("POS-01", backendService.LastTerminalUpsertDeviceCode);
        Assert.Equal("Sandbox", backendService.LastTerminalUpsertRequest?.Environment);
        Assert.Equal("device:POS-01", backendService.LastTerminalUpsertUpdatedBy);
        Assert.Equal("S01", apiResult.Data?.StoreCode);
        Assert.Equal("POS-01", apiResult.Data?.DeviceCode);
        Assert.True(apiResult.Data?.HasSecret);
        Assert.DoesNotContain("secret-pos-01", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"secret\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Active_mode_disables_legacy_credential_endpoints_without_calling_legacy_services()
    {
        var credentialService = new StubLinklyCloudCredentialService();
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        var modeService = new FixedLinklyCloudTerminalModeService("Active");
        await using var factory = new LinklyApiFactory(
            linklyCloudCredentialService: credentialService,
            linklyCloudBackendAsyncService: backendService,
            linklyCloudTerminalService: modeService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var getCredential = await client.GetAsync(
            "/api/v1/linkly/cloud-credential?environment=Sandbox");
        using var upsertCredential = await client.PutAsJsonAsync(
            "/api/v1/linkly/cloud-credential",
            new LinklyCloudCredentialUpsertRequest("Sandbox", "legacy-user", "legacy-password"));
        using var upsertTerminal = await client.PutAsJsonAsync(
            "/api/v1/linkly/cloud-backend/terminal",
            new LinklyCloudBackendTerminalCredentialUpsertRequest(
                "Sandbox",
                "legacy-secret",
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));

        await AssertLegacyModeDisabledAsync(getCredential);
        await AssertLegacyModeDisabledAsync(upsertCredential);
        await AssertLegacyModeDisabledAsync(upsertTerminal);
        Assert.Equal(0, credentialService.GetCalls);
        Assert.Equal(0, credentialService.UpsertCalls);
        Assert.Null(backendService.LastTerminalUpsertRequest);
        Assert.Equal(3, modeService.ModeCalls);
    }

    [Fact]
    public async Task Draft_mode_keeps_legacy_credential_endpoints_compatible()
    {
        var credentialService = new StubLinklyCloudCredentialService(
            responseFactory: (storeCode, environment) => Task.FromResult<LinklyCloudCredentialResponse?>(
                new LinklyCloudCredentialResponse(
                    storeCode,
                    environment,
                    "legacy-user",
                    "legacy-password",
                    DateTimeOffset.UtcNow)));
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        var modeService = new FixedLinklyCloudTerminalModeService("Draft");
        await using var factory = new LinklyApiFactory(
            linklyCloudCredentialService: credentialService,
            linklyCloudBackendAsyncService: backendService,
            linklyCloudTerminalService: modeService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var getCredential = await client.GetAsync(
            "/api/v1/linkly/cloud-credential?environment=Sandbox");
        using var upsertCredential = await client.PutAsJsonAsync(
            "/api/v1/linkly/cloud-credential",
            new LinklyCloudCredentialUpsertRequest("Sandbox", "legacy-user", "legacy-password"));
        using var upsertTerminal = await client.PutAsJsonAsync(
            "/api/v1/linkly/cloud-backend/terminal",
            new LinklyCloudBackendTerminalCredentialUpsertRequest(
                "Sandbox",
                "legacy-secret",
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));

        Assert.Equal(HttpStatusCode.OK, getCredential.StatusCode);
        Assert.Equal(HttpStatusCode.OK, upsertCredential.StatusCode);
        Assert.Equal(HttpStatusCode.OK, upsertTerminal.StatusCode);
        Assert.Equal(1, credentialService.GetCalls);
        Assert.Equal(1, credentialService.UpsertCalls);
        Assert.NotNull(backendService.LastTerminalUpsertRequest);
        Assert.Equal(3, modeService.ModeCalls);
    }

    [Fact]
    public async Task ReceiveCloudBackendNotification_UsesEnvironmentAndSessionRouteOnly()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/linkly/cloud-notifications/Sandbox/session-123/display")
        {
            Content = JsonContent.Create(new
            {
                Response = new
                {
                    DisplayText = new[] { "PRESENT CARD" }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sandbox-notify");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Sandbox", backendService.LastNotificationEnvironment);
        Assert.Equal("session-123", backendService.LastNotificationSessionId);
        Assert.Equal("display", backendService.LastNotificationType);
        Assert.Equal("Bearer sandbox-notify", backendService.LastNotificationAuthorization);
    }

    [Fact]
    public async Task ReceiveCloudBackendNotification_LogsRequestAndResponseAsJson()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        var logger = new RecordingLogger<LinklyController>();
        var controller = new LinklyController(
            new StubLinklyCloudCredentialService(),
            backendService,
            new NoOpLinklyCloudPairingService(),
            logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.ControllerContext.HttpContext.Request.Headers.Authorization = "Bearer sandbox-notify";
        using var payload = JsonDocument.Parse(
            """
            {
              "Response": {
                "ResponseCode": "00",
                "ResponseText": "APPROVED",
                "TxnRef": "260605043452E4C3",
                "CardNumber": "4111111111111111"
              }
            }
            """);

        var result = await controller.ReceiveCloudBackendNotification(
            "Sandbox",
            "session-123",
            "transaction",
            payload.RootElement,
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        using var requestLog = FindLinklyLog(logger.Lines, "notification-transaction", "request");
        Assert.Equal("api-linkly-controller", requestLog.RootElement.GetProperty("source").GetString());
        Assert.Equal("request", requestLog.RootElement.GetProperty("direction").GetString());
        Assert.Equal("Sandbox", requestLog.RootElement.GetProperty("environment").GetString());
        Assert.Equal("session-123", requestLog.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.Null, requestLog.RootElement.GetProperty("request").ValueKind);
        var requestCallback = requestLog.RootElement.GetProperty("details").GetProperty("callback");
        Assert.True(requestCallback.GetProperty("hasPayload").GetBoolean());
        Assert.Equal("Object", requestCallback.GetProperty("payloadKind").GetString());
        Assert.Equal(JsonValueKind.Null, requestCallback.GetProperty("cardNumber").ValueKind);
        Assert.DoesNotContain("sandbox-notify", logger.Lines, StringComparer.Ordinal);
        Assert.DoesNotContain("APPROVED", logger.Lines, StringComparer.Ordinal);
        Assert.DoesNotContain("4111111111111111", logger.Lines, StringComparer.Ordinal);
        using var responseLog = FindLinklyLog(logger.Lines, "notification-transaction", "response");
        Assert.Equal(200, responseLog.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.True(responseLog.RootElement.GetProperty("response").GetProperty("Success").GetBoolean());
        Assert.Equal("accepted", responseLog.RootElement.GetProperty("response").GetProperty("Data").GetString());
        Assert.Equal("****1111", responseLog.RootElement.GetProperty("details").GetProperty("callback").GetProperty("cardNumber").GetString());
    }

    [Fact]
    public async Task SendCloudBackendKey_ReturnsBadRequestWhenLinklyRejectsActionButKeepsSessionPending()
    {
        var sendKeyResponse = CreateBackendResponse("session-key-400", "Pending") with
        {
            LastHttpStatus = 400
        };
        var backendService = new CapturingLinklyCloudBackendAsyncService(sendKeyResponse: sendKeyResponse);
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions/session-key-400/sendkey",
            new LinklyCloudBackendSendKeyRequest("Sandbox", "0", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("S01", backendService.LastSendKeyStoreCode);
        Assert.Equal("POS-01", backendService.LastSendKeyDeviceCode);
        Assert.Equal("session-key-400", backendService.LastSendKeySessionId);
        Assert.Equal("0", backendService.LastSendKeyRequest?.Key);
    }

    [Fact]
    public async Task MarkCloudBackendReceiptPrinted_UsesAuthenticatedDeviceClaimsOnly()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions/session-printed/receipt/printed?storeCode=S99&deviceCode=POS-99",
            new
            {
                Environment = "Sandbox",
                StoreCode = "S99",
                DeviceCode = "POS-99"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastMarkReceiptPrintedStoreCode);
        Assert.Equal("POS-01", backendService.LastMarkReceiptPrintedDeviceCode);
        Assert.Equal("session-printed", backendService.LastMarkReceiptPrintedSessionId);
        Assert.Equal("Sandbox", backendService.LastMarkReceiptPrintedEnvironment);
    }

    [Fact]
    public async Task AcknowledgeCloudBackendTransaction_UsesAuthenticatedDeviceClaimsAndBodyEnvironment()
    {
        var expected = CreateBackendResponse("session-ack", "Completed") with
        {
            ClientAcknowledgedAt = new DateTimeOffset(2026, 6, 5, 1, 2, 3, TimeSpan.Zero)
        };
        var backendService = new CapturingLinklyCloudBackendAsyncService(acknowledgeResponse: expected);
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions/session-ack/acknowledge?environment=Production&storeCode=S99&deviceCode=POS-99",
            new LinklyCloudBackendAcknowledgeRequest("Sandbox"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", backendService.LastAcknowledgeStoreCode);
        Assert.Equal("POS-01", backendService.LastAcknowledgeDeviceCode);
        Assert.Equal("session-ack", backendService.LastAcknowledgeSessionId);
        Assert.Equal("Sandbox", backendService.LastAcknowledgeEnvironment);
        Assert.False(backendService.LastAcknowledgeSupervisorResolved);
        Assert.NotNull(apiResult.Data?.ClientAcknowledgedAt);
    }

    [Theory]
    [InlineData("{ \"environment\": \"Sandbox\", \"supervisorResolved\": true }", true)]
    [InlineData("{ \"environment\": \"Sandbox\", \"supervisorResolved\": false }", false)]
    [InlineData("{ \"environment\": \"Sandbox\" }", false)]
    [InlineData(null, false)]
    public async Task AcknowledgeCloudBackendTransaction_PassesSupervisorResolutionFlagFromBody(
        string? body,
        bool expected)
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService();
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsync(
            "/api/v1/linkly/cloud-backend/transactions/session-ack/acknowledge?environment=Sandbox",
            body is null ? null : new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // 旧客户端不带标记或空请求体时必须保持普通 ack，不能被当成主管结案。
        Assert.Equal(expected, backendService.LastAcknowledgeSupervisorResolved);
    }

    [Fact]
    public async Task AcknowledgeCloudBackendTransaction_ReturnsNotFoundWhenSessionIsMissing()
    {
        var backendService = new CapturingLinklyCloudBackendAsyncService(
            acknowledgeException: new LinklyCloudBackendSessionNotFoundException());
        await using var factory = new LinklyApiFactory(linklyCloudBackendAsyncService: backendService);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.PostAsync(
            "/api/v1/linkly/cloud-backend/transactions/missing-session/acknowledge?environment=Sandbox",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudBackendSessionResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND", apiResult.ErrorCode);
    }


    [Fact]
    public async Task GetCloudCredential_ReturnsStableNotFoundWhenCredentialIsMissing()
    {
        await using var factory = new LinklyApiFactory(new StubLinklyCloudCredentialService(
            responseFactory: (_, _) => Task.FromResult<LinklyCloudCredentialResponse?>(null)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential?environment=Production");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudCredentialResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_CREDENTIAL_NOT_CONFIGURED", apiResult.ErrorCode);
        Assert.Equal("Linkly Cloud credential is not configured for this store.", apiResult.Message);
    }

    [Fact]
    public async Task GetCloudCredential_ReturnsSanitizedServerErrorWhenServiceThrows()
    {
        const string secretPassword = "merchant-password";
        await using var factory = new LinklyApiFactory(new StubLinklyCloudCredentialService(
            exceptionFactory: (_, _) => new InvalidOperationException($"SQL timeout from POSM_LinklyCloudCredential on db01 for password {secretPassword}")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential?environment=Production");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudCredentialResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_CREDENTIAL_READ_FAILED", apiResult.ErrorCode);
        var message = apiResult.Message ?? string.Empty;
        Assert.Equal("Failed to load Linkly Cloud credential configuration.", message);
        Assert.DoesNotContain("SQL", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("POSM_LinklyCloudCredential", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db01", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretPassword, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_FailsWhenLinklyCloudCredentialSchemaInitializerThrows()
    {
        using var factory = new LinklyApiFactory(
            schemaInitializer: new ThrowingLinklyCloudCredentialSchemaInitializer(
                new InvalidOperationException("linkly cloud schema bootstrap failed")));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("schema bootstrap failed", exception.Message);
    }

    private static JsonDocument FindLinklyLog(
        IReadOnlyList<string> lines,
        string operation,
        string phase)
    {
        foreach (var line in lines)
        {
            var jsonStart = line.IndexOf('{', StringComparison.Ordinal);
            if (jsonStart < 0)
            {
                continue;
            }

            var document = JsonDocument.Parse(line[jsonStart..]);
            if (string.Equals(document.RootElement.GetProperty("operation").GetString(), operation, StringComparison.Ordinal) &&
                string.Equals(document.RootElement.GetProperty("phase").GetString(), phase, StringComparison.Ordinal))
            {
                return document;
            }

            document.Dispose();
        }

        throw new Xunit.Sdk.XunitException($"Expected Linkly JSON log operation={operation} phase={phase}.");
    }

    private static async Task AssertLegacyModeDisabledAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResult<JsonElement>>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("LINKLY_CLOUD_LEGACY_MODE_DISABLED", envelope.ErrorCode);
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

    private sealed class LinklyApiFactory(
        ILinklyCloudCredentialService? linklyCloudCredentialService = null,
        ILinklyCloudCredentialSchemaInitializer? schemaInitializer = null,
        ILinklyCloudBackendAsyncService? linklyCloudBackendAsyncService = null,
        ILinklyCloudBackendAsyncSchemaInitializer? backendAsyncSchemaInitializer = null,
        ILinklyCloudTerminalService? linklyCloudTerminalService = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
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

                services.RemoveAll<ILinklyCloudCredentialService>();
                services.AddSingleton(linklyCloudCredentialService ?? new StubLinklyCloudCredentialService());

                services.RemoveAll<ILinklyCloudBackendAsyncService>();
                services.AddSingleton(linklyCloudBackendAsyncService ?? new CapturingLinklyCloudBackendAsyncService());

                services.RemoveAll<ILinklyCloudTerminalService>();
                services.AddSingleton(linklyCloudTerminalService ?? new FixedLinklyCloudTerminalModeService("Legacy"));

                services.RemoveAll<ILinklyCloudPairingService>();
                services.AddSingleton<ILinklyCloudPairingService>(new NoOpLinklyCloudPairingService());

                services.RemoveAll<IStoreSchemaInitializer>();
                services.AddSingleton<IStoreSchemaInitializer>(new NoOpStoreSchemaInitializer());

                services.RemoveAll<IAttendanceQrKeySchemaInitializer>();
                services.AddSingleton<IAttendanceQrKeySchemaInitializer>(new TestNoOpAttendanceQrKeySchemaInitializer());

                services.RemoveAll<IDeviceRuntimeStatusSchemaInitializer>();
                services.AddSingleton<IDeviceRuntimeStatusSchemaInitializer>(new NoOpDeviceRuntimeStatusSchemaInitializer());

                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton(schemaInitializer ?? new NoOpLinklyCloudCredentialSchemaInitializer());

                services.RemoveAll<ILinklyCloudBackendAsyncSchemaInitializer>();
                services.AddSingleton(backendAsyncSchemaInitializer ?? new NoOpLinklyCloudBackendAsyncSchemaInitializer());

                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton<ISquareTokenSchemaInitializer>(new NoOpSquareTokenSchemaInitializer());

                services.RemoveAll<IAdvertisementSchemaInitializer>();
                services.AddSingleton<IAdvertisementSchemaInitializer>(new NoOpAdvertisementSchemaInitializer());
            });
        }
    }

    private sealed class NoOpLinklyCloudCredentialSchemaInitializer : ILinklyCloudCredentialSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLinklyCloudCredentialSchemaInitializer(Exception exception) : ILinklyCloudCredentialSchemaInitializer
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

    private sealed class NoOpLinklyCloudBackendAsyncSchemaInitializer : ILinklyCloudBackendAsyncSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSquareTokenSchemaInitializer : ISquareTokenSchemaInitializer
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
        public const string SchemeName = "LinklyTestAuth";

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

    private static string? GetRoute<TAttribute>(string methodName)
        where TAttribute : HttpMethodAttribute
    {
        return typeof(LinklyController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(TAttribute), inherit: false)
            .Cast<TAttribute>()
            .Single()
            .Template;
    }

    private static LinklyCloudBackendSessionResponse CreateBackendResponse(
        string sessionId,
        string status)
    {
        return new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S01",
            "POS-01",
            sessionId,
            status,
            "SERVER-TXN",
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            [],
            null,
            0,
            null,
            null,
            null,
            []);
    }

    private sealed class CapturingLinklyCloudBackendAsyncService(
        Exception? startException = null,
        LinklyCloudBackendSessionResponse? activeResponse = null,
        LinklyCloudBackendSessionResponse? resumableResponse = null,
        LinklyCloudBackendHealthResponse? healthResponse = null,
        LinklyCloudBackendSessionResponse? sendKeyResponse = null,
        LinklyCloudBackendSessionResponse? acknowledgeResponse = null,
        Exception? acknowledgeException = null,
        Exception? terminalTestException = null,
        Exception? statusException = null) : ILinklyCloudBackendAsyncService
    {
        public string? LastStoreCode { get; private set; }

        public string? LastDeviceCode { get; private set; }

        public string? LastActiveStoreCode { get; private set; }

        public string? LastActiveDeviceCode { get; private set; }

        public string? LastActiveEnvironment { get; private set; }

        public string? LastResumableStoreCode { get; private set; }

        public string? LastResumableDeviceCode { get; private set; }

        public string? LastResumableEnvironment { get; private set; }

        public string? LastHealthStoreCode { get; private set; }

        public string? LastHealthDeviceCode { get; private set; }

        public string? LastHealthEnvironment { get; private set; }

        public string? LastStatusTestStoreCode { get; private set; }

        public string? LastStatusTestDeviceCode { get; private set; }

        public string? LastStatusTestEnvironment { get; private set; }

        public string? LastLogonTestStoreCode { get; private set; }

        public string? LastLogonTestDeviceCode { get; private set; }

        public string? LastLogonTestEnvironment { get; private set; }

        public string? LastTerminalUpsertStoreCode { get; private set; }

        public string? LastTerminalUpsertDeviceCode { get; private set; }

        public string? LastTerminalUpsertUpdatedBy { get; private set; }

        public LinklyCloudBackendTerminalCredentialUpsertRequest? LastTerminalUpsertRequest { get; private set; }

        public string? LastNotificationEnvironment { get; private set; }

        public string? LastNotificationSessionId { get; private set; }

        public string? LastNotificationType { get; private set; }

        public string? LastNotificationAuthorization { get; private set; }

        public LinklyCloudBackendTransactionRequest? LastStartRequest { get; private set; }

        public string? LastSendKeyStoreCode { get; private set; }

        public string? LastSendKeyDeviceCode { get; private set; }

        public string? LastSendKeySessionId { get; private set; }

        public LinklyCloudBackendSendKeyRequest? LastSendKeyRequest { get; private set; }

        public string? LastMarkReceiptPrintedStoreCode { get; private set; }

        public string? LastMarkReceiptPrintedDeviceCode { get; private set; }

        public string? LastMarkReceiptPrintedSessionId { get; private set; }

        public string? LastMarkReceiptPrintedEnvironment { get; private set; }

        public string? LastAcknowledgeStoreCode { get; private set; }

        public string? LastAcknowledgeDeviceCode { get; private set; }

        public string? LastAcknowledgeEnvironment { get; private set; }

        public string? LastAcknowledgeSessionId { get; private set; }

        public bool? LastAcknowledgeSupervisorResolved { get; private set; }

        public Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
            string storeCode,
            string deviceCode,
            LinklyCloudBackendTransactionRequest request,
            CancellationToken cancellationToken)
        {
            if (startException is not null)
            {
                throw startException;
            }

            LastStoreCode = storeCode;
            LastDeviceCode = deviceCode;
            LastStartRequest = request;
            return Task.FromResult(CreateBackendResponse(Guid.NewGuid().ToString("D"), "Pending"));
        }

        public Task<LinklyCloudBackendSessionResponse?> GetStatusAsync(
            string storeCode,
            string deviceCode,
            string environment,
            string sessionId,
            CancellationToken cancellationToken)
        {
            throw statusException ?? new NotImplementedException();
        }

        public Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken)
        {
            LastActiveStoreCode = storeCode;
            LastActiveDeviceCode = deviceCode;
            LastActiveEnvironment = environment;
            return Task.FromResult(activeResponse);
        }

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken)
        {
            LastResumableStoreCode = storeCode;
            LastResumableDeviceCode = deviceCode;
            LastResumableEnvironment = environment;
            return Task.FromResult(resumableResponse);
        }

        public Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken)
        {
            LastHealthStoreCode = storeCode;
            LastHealthDeviceCode = deviceCode;
            LastHealthEnvironment = environment;
            return Task.FromResult(healthResponse ?? new LinklyCloudBackendHealthResponse(
                "Sandbox",
                storeCode,
                deviceCode,
                false,
                null,
                [new LinklyCloudBackendHealthCheckDto("STORE_CREDENTIAL", false, "missing")]));
        }

        public Task<LinklyCloudBackendStatusTestResponse> RunStatusTestAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken)
        {
            if (terminalTestException is not null)
            {
                throw terminalTestException;
            }

            LastStatusTestStoreCode = storeCode;
            LastStatusTestDeviceCode = deviceCode;
            LastStatusTestEnvironment = environment;
            return Task.FromResult(new LinklyCloudBackendStatusTestResponse(
                "Sandbox",
                storeCode,
                deviceCode,
                "status-session-1",
                new DateTimeOffset(2026, 6, 5, 4, 0, 0, TimeSpan.Zero),
                200,
                true,
                "00",
                "APPROVED",
                null,
                "050626",
                "140000",
                "ANZ Linkly Cloud transaction status test succeeded."));
        }

        public Task<LinklyCloudBackendLogonTestResponse> RunLogonTestAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken)
        {
            if (terminalTestException is not null)
            {
                throw terminalTestException;
            }

            LastLogonTestStoreCode = storeCode;
            LastLogonTestDeviceCode = deviceCode;
            LastLogonTestEnvironment = environment;
            return Task.FromResult(new LinklyCloudBackendLogonTestResponse(
                "Sandbox",
                storeCode,
                deviceCode,
                "logon-session-1",
                new DateTimeOffset(2026, 6, 5, 4, 0, 0, TimeSpan.Zero),
                200,
                true,
                "00",
                "APPROVED",
                "12345678",
                "123456789012345",
                "1.8.6.0",
                "ANZ Linkly Cloud logon succeeded."));
        }

        public Task<LinklyCloudBackendTerminalCredentialResponse> UpsertTerminalCredentialAsync(
            string storeCode,
            string deviceCode,
            LinklyCloudBackendTerminalCredentialUpsertRequest request,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            LastTerminalUpsertStoreCode = storeCode;
            LastTerminalUpsertDeviceCode = deviceCode;
            LastTerminalUpsertRequest = request;
            LastTerminalUpsertUpdatedBy = updatedBy;
            return Task.FromResult(new LinklyCloudBackendTerminalCredentialResponse(
                request.Environment,
                storeCode,
                deviceCode,
                true,
                request.PosId,
                new DateTimeOffset(2026, 6, 2, 2, 0, 0, TimeSpan.Zero)));
        }

        public Task<LinklyCloudBackendSessionResponse> RecoverAsync(
            string storeCode,
            string deviceCode,
            string sessionId,
            LinklyCloudBackendRecoverRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
            string storeCode,
            string deviceCode,
            string sessionId,
            LinklyCloudBackendSendKeyRequest request,
            CancellationToken cancellationToken)
        {
            LastSendKeyStoreCode = storeCode;
            LastSendKeyDeviceCode = deviceCode;
            LastSendKeySessionId = sessionId;
            LastSendKeyRequest = request;
            return Task.FromResult(sendKeyResponse ?? CreateBackendResponse(sessionId, "Pending"));
        }

        public Task<LinklyCloudBackendSessionResponse> MarkReceiptPrintedAsync(
            string storeCode,
            string deviceCode,
            string sessionId,
            LinklyCloudBackendMarkReceiptPrintedRequest request,
            CancellationToken cancellationToken)
        {
            LastMarkReceiptPrintedStoreCode = storeCode;
            LastMarkReceiptPrintedDeviceCode = deviceCode;
            LastMarkReceiptPrintedSessionId = sessionId;
            LastMarkReceiptPrintedEnvironment = request.Environment;
            return Task.FromResult(CreateBackendResponse(sessionId, "Completed"));
        }

        public Task<LinklyCloudBackendSessionResponse> AcknowledgeSessionAsync(
            string storeCode,
            string deviceCode,
            string environment,
            string sessionId,
            bool supervisorResolved,
            CancellationToken cancellationToken)
        {
            if (acknowledgeException is not null)
            {
                throw acknowledgeException;
            }

            LastAcknowledgeStoreCode = storeCode;
            LastAcknowledgeDeviceCode = deviceCode;
            LastAcknowledgeEnvironment = environment;
            LastAcknowledgeSessionId = sessionId;
            LastAcknowledgeSupervisorResolved = supervisorResolved;
            return Task.FromResult(acknowledgeResponse ?? CreateBackendResponse(sessionId, "Completed"));
        }

        public Task ReceiveNotificationAsync(
            string environment,
            string sessionId,
            string type,
            string? authorizationHeader,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            LastNotificationEnvironment = environment;
            LastNotificationSessionId = sessionId;
            LastNotificationType = type;
            LastNotificationAuthorization = authorizationHeader;
            return Task.CompletedTask;
        }
    }

    private sealed class StubLinklyCloudCredentialService(
        Func<string, string, Task<LinklyCloudCredentialResponse?>>? responseFactory = null,
        Func<string, string, Exception>? exceptionFactory = null,
        Func<string, LinklyCloudCredentialUpsertRequest, string?, Task<LinklyCloudCredentialUpsertResponse>>? upsertFactory = null) : ILinklyCloudCredentialService
    {
        public int GetCalls { get; private set; }

        public int UpsertCalls { get; private set; }

        public Task<LinklyCloudCredentialResponse?> GetByStoreCodeAsync(
            string storeCode,
            string environment,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            if (exceptionFactory is not null)
            {
                throw exceptionFactory(storeCode, environment);
            }

            if (responseFactory is not null)
            {
                return responseFactory(storeCode, environment);
            }

            return Task.FromResult<LinklyCloudCredentialResponse?>(null);
        }

        public Task<LinklyCloudCredentialUpsertResponse> UpsertAsync(
            string storeCode,
            LinklyCloudCredentialUpsertRequest request,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            UpsertCalls++;
            if (upsertFactory is not null)
            {
                return upsertFactory(storeCode, request, updatedBy);
            }

            return Task.FromResult(new LinklyCloudCredentialUpsertResponse(
                storeCode,
                request.Environment,
                request.Username,
                true,
                new DateTimeOffset(2026, 6, 2, 1, 0, 0, TimeSpan.Zero)));
        }
    }
}
