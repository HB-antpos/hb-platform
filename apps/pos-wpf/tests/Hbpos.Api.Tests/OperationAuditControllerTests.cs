using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Hbpos.Api;
using BlazorApp.Shared.DTOs;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hbpos.Api.Tests;

public sealed class OperationAuditControllerTests
{
    [Fact]
    public async Task Batch_requires_authenticated_device_claims()
    {
        var service = new RecordingOperationAuditIngestService();
        var controller = CreateController(service);

        var action = await controller.Batch(CreateRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(action.Result);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Batch_rejects_event_scope_that_differs_from_device_claims()
    {
        var service = new RecordingOperationAuditIngestService();
        var controller = CreateController(service, "STORE-1", "POS-1");
        var request = CreateRequest();
        request.Events[0].StoreCode = "STORE-2";

        var action = await controller.Batch(request, CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Batch_requires_exact_case_sensitive_scope_match()
    {
        var service = new RecordingOperationAuditIngestService();
        var controller = CreateController(service, "STORE-1", "POS-1");
        var request = CreateRequest();
        request.Events[0].StoreCode = "store-1";

        var action = await controller.Batch(request, CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Batch_passes_authoritative_claim_scope_to_ingest_service()
    {
        var service = new RecordingOperationAuditIngestService();
        var controller = CreateController(service, "STORE-1", "POS-1");
        var request = CreateRequest();

        var action = await controller.Batch(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<OperationAuditBatchResultDto>(ok.Value);
        Assert.Equal(1, payload.AcceptedCount);
        Assert.Same(request, service.Request);
        Assert.Equal("STORE-1", service.StoreCode);
        Assert.Equal("POS-1", service.DeviceCode);
    }

    [Fact]
    public async Task Batch_passes_trusted_device_system_claim_to_ingest_service()
    {
        var service = new RecordingOperationAuditIngestService();
        var controller = CreateController(service, "STORE-1", "POS-1", DeviceSystems.IpadOs);

        var action = await controller.Batch(CreateRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(DeviceSystems.IpadOs, service.DeviceSystem);
    }

    [Fact]
    public async Task Batch_rejects_more_than_one_hundred_events()
    {
        var service = new RecordingOperationAuditIngestService();
        var controller = CreateController(service, "STORE-1", "POS-1");
        var request = new OperationAuditBatchRequestDto
        {
            Events = Enumerable.Range(0, 101)
                .Select(_ => CreateRequest().Events[0])
                .ToList()
        };

        var action = await controller.Batch(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Batch_rejects_request_larger_than_four_mebibytes()
    {
        var service = new RecordingOperationAuditIngestService();
        var controller = CreateController(service, "STORE-1", "POS-1");
        controller.HttpContext.Request.ContentLength = (4 * 1024 * 1024) + 1;

        var action = await controller.Batch(CreateRequest(), CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Batch_rejects_null_event_in_payload()
    {
        var service = new RecordingOperationAuditIngestService();
        var controller = CreateController(service, "STORE-1", "POS-1");
        var request = CreateRequest();
        request.Events[0] = null!;

        var action = await controller.Batch(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Batch_http_pipeline_requires_bearer_and_device_headers_then_uses_authenticated_scope()
    {
        var service = new RecordingOperationAuditIngestService();
        await using var factory = new OperationAuditApiFactory(service);
        using var client = factory.CreateClient();

        var unauthorized = await client.PostAsJsonAsync(
            "/api/v1/operation-audits/batch",
            CreateRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Null(service.Request);

        using var authenticatedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/operation-audits/batch")
        {
            Content = JsonContent.Create(CreateRequest())
        };
        authenticatedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-device-token");
        authenticatedRequest.Headers.Add(DeviceAuthConstants.StoreCodeHeader, "STORE-1");
        authenticatedRequest.Headers.Add(DeviceAuthConstants.DeviceCodeHeader, "POS-1");
        authenticatedRequest.Headers.Add(DeviceAuthConstants.HardwareIdHeader, "HW-1");

        var response = await client.SendAsync(authenticatedRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<OperationAuditBatchResultDto>();
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.AcceptedCount);
        Assert.Equal("STORE-1", service.StoreCode);
        Assert.Equal("POS-1", service.DeviceCode);
        Assert.Equal(DeviceSystems.Windows, service.DeviceSystem);
    }

    [Fact]
    public async Task Batch_http_pipeline_accepts_null_items_from_handheld_non_order_events()
    {
        // 已发布的手持/iPad 对非订单事件（如 CASHIER_LOGIN）上传 "items": null；
        // 若被隐式必填校验整批 400，客户端会把同批事件全部永久标为 rejected。
        var service = new RecordingOperationAuditIngestService();
        await using var factory = new OperationAuditApiFactory(service);
        using var client = factory.CreateClient();
        var json = $$"""
            {"events":[{"eventId":"{{Guid.NewGuid()}}","schemaVersion":1,
            "occurredAtUtc":"2026-09-30T05:16:54.678Z","operationType":"CASHIER_LOGIN",
            "outcome":"Succeeded","cashierId":"89adde426db243a9b5aedb9b863fb50f","userGuid":null,
            "cashierName":"管理员","isOfflineCached":false,"isEmergencyOverride":false,
            "storeCode":"STORE-1","deviceCode":"POS-1","appVersion":"0.1.0","instanceId":null,
            "orderGuid":null,"correlationId":"{{Guid.NewGuid()}}","currencyCode":"AUD",
            "properties":null,"items":null}]}
            """;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/operation-audits/batch")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid-device-token");
        request.Headers.Add(DeviceAuthConstants.StoreCodeHeader, "STORE-1");
        request.Headers.Add(DeviceAuthConstants.DeviceCodeHeader, "POS-1");
        request.Headers.Add(DeviceAuthConstants.HardwareIdHeader, "HW-1");

        var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.NotNull(service.Request);
        Assert.Empty(service.Request!.Events[0].Items);
    }

    [Fact]
    public async Task Batch_http_pipeline_returns_structured_device_disabled_code()
    {
        var service = new RecordingOperationAuditIngestService();
        await using var factory = new OperationAuditApiFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/operation-audits/batch")
        {
            Content = JsonContent.Create(CreateRequest())
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "disabled-device-token");
        request.Headers.Add(DeviceAuthConstants.StoreCodeHeader, "STORE-1");
        request.Headers.Add(DeviceAuthConstants.DeviceCodeHeader, "POS-1");
        request.Headers.Add(DeviceAuthConstants.HardwareIdHeader, "HW-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResult<object>>();
        Assert.NotNull(payload);
        Assert.False(payload!.Success);
        Assert.Equal(DeviceAuthorizationFailureCodes.DeviceDisabled, payload.ErrorCode);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Batch_logs_rejected_events_as_warning_with_codes()
    {
        var logger = new RecordingLogger<OperationAuditsController>();
        var controller = CreateController(new RejectingOperationAuditIngestService(), "STORE-1", "POS-1", logger: logger);

        var result = await controller.Batch(CreateRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Equal("OperationAudit:EVENTS-REJECTED", entry.EventId.Name);
        Assert.Contains("store=STORE-1 device=POS-1 rejected=1 total=1 codes=INVALID_OPERATION_TYPE", entry.Message);
    }

    [Fact]
    public async Task Batch_logs_scope_mismatch_as_warning()
    {
        var logger = new RecordingLogger<OperationAuditsController>();
        var controller = CreateController(new RecordingOperationAuditIngestService(), "STORE-2", "POS-1", logger: logger);

        await controller.Batch(CreateRequest(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("OperationAudit:DEVICE-SCOPE-FORBIDDEN", entry.EventId.Name);
        Assert.Contains("status=403 code=DEVICE_SCOPE_FORBIDDEN store=STORE-2 device=POS-1 events=1", entry.Message);
    }

    private static OperationAuditsController CreateController(
        IOperationAuditIngestService service,
        string? storeCode = null,
        string? deviceCode = null,
        string? deviceSystem = null,
        Microsoft.Extensions.Logging.ILogger<OperationAuditsController>? logger = null)
    {
        var httpContext = new DefaultHttpContext();
        if (storeCode is not null && deviceCode is not null)
        {
            var claims = new List<Claim>
            {
                new(DeviceAuthConstants.StoreCodeClaim, storeCode),
                new(DeviceAuthConstants.DeviceCodeClaim, deviceCode)
            };
            if (!string.IsNullOrWhiteSpace(deviceSystem))
            {
                claims.Add(new Claim(DeviceAuthConstants.DeviceSystemClaim, deviceSystem));
            }

            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, DeviceAuthConstants.Scheme));
        }

        return new OperationAuditsController(service, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private sealed class RejectingOperationAuditIngestService : IOperationAuditIngestService
    {
        public Task<OperationAuditBatchResultDto> IngestAsync(
            OperationAuditBatchRequestDto request,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken,
            string? deviceSystem = null)
        {
            return Task.FromResult(new OperationAuditBatchResultDto
            {
                RejectedCount = request.Events.Count,
                Results = request.Events.Select(x => new OperationAuditItemResultDto
                {
                    EventId = x.EventId,
                    Status = "rejected",
                    ErrorCode = "INVALID_OPERATION_TYPE",
                    ErrorMessage = "operationType is not supported."
                }).ToList()
            });
        }
    }

    private static OperationAuditBatchRequestDto CreateRequest()
    {
        return new OperationAuditBatchRequestDto
        {
            Events =
            [
                new OperationAuditEventDto
                {
                    EventId = Guid.NewGuid(),
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    OperationType = "CASH_DRAWER_OPEN",
                    Outcome = "Succeeded",
                    StoreCode = "STORE-1",
                    DeviceCode = "POS-1"
                }
            ]
        };
    }

    private sealed class RecordingOperationAuditIngestService : IOperationAuditIngestService
    {
        public OperationAuditBatchRequestDto? Request { get; private set; }

        public string? StoreCode { get; private set; }

        public string? DeviceCode { get; private set; }

        public string? DeviceSystem { get; private set; }

        public Task<OperationAuditBatchResultDto> IngestAsync(
            OperationAuditBatchRequestDto request,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken,
            string? deviceSystem = null)
        {
            Request = request;
            StoreCode = storeCode;
            DeviceCode = deviceCode;
            DeviceSystem = deviceSystem;
            return Task.FromResult(new OperationAuditBatchResultDto
            {
                AcceptedCount = request.Events.Count,
                Results = request.Events.Select(x => new OperationAuditItemResultDto
                {
                    EventId = x.EventId,
                    Status = "accepted"
                }).ToList()
            });
        }
    }

    private sealed class OperationAuditApiFactory(IOperationAuditIngestService ingestService)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeviceAuthorizationService>();
                services.AddSingleton<IDeviceAuthorizationService>(new TestDeviceAuthorizationService());
                services.RemoveAll<IOperationAuditIngestService>();
                services.AddSingleton(ingestService);
                services.RemoveAll<IStoreSchemaInitializer>();
                services.AddSingleton<IStoreSchemaInitializer>(new NoOpStoreSchemaInitializer());
                services.RemoveAll<IAttendanceQrKeySchemaInitializer>();
                services.AddSingleton<IAttendanceQrKeySchemaInitializer>(new TestNoOpAttendanceQrKeySchemaInitializer());
                services.RemoveAll<IAdvertisementSchemaInitializer>();
                services.AddSingleton<IAdvertisementSchemaInitializer>(new NoOpAdvertisementSchemaInitializer());
                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton<ILinklyCloudCredentialSchemaInitializer>(new NoOpLinklyCloudCredentialSchemaInitializer());
                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton<ISquareTokenSchemaInitializer>(new NoOpSquareTokenSchemaInitializer());
                services.RemoveAll<IOperationAuditSchemaInitializer>();
                services.AddSingleton<IOperationAuditSchemaInitializer>(new TestNoOpOperationAuditSchemaInitializer());
                services.RemoveAll<IDeviceRuntimeStatusSchemaInitializer>();
                services.AddSingleton<IDeviceRuntimeStatusSchemaInitializer>(new NoOpDeviceRuntimeStatusSchemaInitializer());
                services.RemoveAll<ILinklyCloudBackendAsyncSchemaInitializer>();
                services.AddSingleton<ILinklyCloudBackendAsyncSchemaInitializer>(new NoOpLinklyCloudBackendAsyncSchemaInitializer());
                services.RemoveAll<ISquareWebhookSchemaInitializer>();
                services.AddSingleton<ISquareWebhookSchemaInitializer>(new NoOpSquareWebhookSchemaInitializer());
            });
        }
    }

    private sealed class TestDeviceAuthorizationService : IDeviceAuthorizationService
    {
        public Task<DeviceAuthorizationValidationResult> ValidateAsync(
            string authorizationCode,
            string deviceCode,
            string storeCode,
            string? hardwareId,
            CancellationToken cancellationToken)
        {
            var result =
                authorizationCode == "disabled-device-token"
                    ? DeviceAuthorizationValidationResult.Failed(
                        DeviceAuthorizationFailureCodes.DeviceDisabled)
                    : authorizationCode == "valid-device-token" &&
                deviceCode == "POS-1" &&
                storeCode == "STORE-1" &&
                hardwareId == "HW-1"
                    ? DeviceAuthorizationValidationResult.Authorized(
                        new DeviceAuthorizationResult(deviceCode, storeCode, hardwareId))
                    : DeviceAuthorizationValidationResult.Failed(
                        DeviceAuthorizationFailureCodes.Invalid);
            return Task.FromResult(result);
        }
    }

    private sealed class NoOpStoreSchemaInitializer : IStoreSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpAdvertisementSchemaInitializer : IAdvertisementSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpLinklyCloudCredentialSchemaInitializer : ILinklyCloudCredentialSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpSquareTokenSchemaInitializer : ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpDeviceRuntimeStatusSchemaInitializer : IDeviceRuntimeStatusSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpLinklyCloudBackendAsyncSchemaInitializer : ILinklyCloudBackendAsyncSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpSquareWebhookSchemaInitializer : ISquareWebhookSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
