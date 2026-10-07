using System.Reflection;
using System.Security.Claims;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Hbpos.Api.Tests;

public sealed class LinklySettlementsControllerTests
{
    [Fact]
    public void Controller_uses_device_authentication_without_cashier_permission_policy()
    {
        var authorize = Assert.Single(
            typeof(LinklySettlementsController).GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal(DeviceAuthConstants.Scheme, authorize.AuthenticationSchemes);
        Assert.Null(authorize.Policy);
    }

    [Fact]
    public async Task Sync_requires_authenticated_device_scope()
    {
        var service = new RecordingSyncService();
        var controller = CreateController(service);

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Sync_rejects_body_scope_that_differs_from_claims()
    {
        var service = new RecordingSyncService();
        var controller = CreateController(service, "S001", "POS-01");
        var request = CreateRequest() with { StoreCode = "S002" };

        var result = await controller.Sync(request, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Sync_passes_authoritative_claim_scope_to_service()
    {
        var service = new RecordingSyncService();
        var controller = CreateController(service, "S001", "POS-01");

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<LinklySettlementSyncResponse>(ok.Value);
        Assert.Equal("S001", service.StoreCode);
        Assert.Equal("POS-01", service.DeviceCode);
    }

    [Fact]
    public async Task Sync_maps_revision_conflict_to_http_409()
    {
        var service = new RecordingSyncService
        {
            Exception = new LinklySettlementConflictException("REVISION_CONTENT_CONFLICT", "conflict")
        };
        var controller = CreateController(service, "S001", "POS-01");

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Sync_logs_conflict_detail_as_warning_without_returning_it_to_the_client()
    {
        var detail = "field=RequestedAtUtc stored=2026-10-02T06:48:52.9566667Z incoming=2026-10-02T06:48:52.9574255Z";
        var service = new RecordingSyncService
        {
            Exception = new LinklySettlementConflictException(
                "IMMUTABLE_FIELDS_CONFLICT",
                "Immutable Linkly settlement fields cannot change.",
                detail)
        };
        var logger = new RecordingLogger<LinklySettlementsController>();
        var controller = CreateController(service, "S001", "POS-01", logger);
        var request = CreateRequest() with { ClientRevision = 5 };

        var result = await controller.Sync(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        // 响应体保持原契约（code、message），冲突明细只进服务端日志。
        Assert.Equal(["code", "message"], conflict.Value!.GetType().GetProperties().Select(property => property.Name));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("LinklySettlementSync:IMMUTABLE-FIELDS-CONFLICT", entry.EventId.Name);
        Assert.Equal(
            $"Linkly settlement sync rejected status=409 code=IMMUTABLE_FIELDS_CONFLICT store=S001 device=POS-01 settlement={request.SettlementGuid} revision=5 detail={detail}",
            entry.Message);
    }

    [Fact]
    public async Task Sync_logs_validation_rejection_as_warning()
    {
        var service = new RecordingSyncService
        {
            Exception = new LinklySettlementValidationException("INVALID_CLIENT_REVISION", "clientRevision must be greater than zero.")
        };
        var logger = new RecordingLogger<LinklySettlementsController>();
        var controller = CreateController(service, "S001", "POS-01", logger);

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("LinklySettlementSync:INVALID-CLIENT-REVISION", entry.EventId.Name);
        Assert.Contains("status=400 code=INVALID_CLIENT_REVISION store=S001 device=POS-01", entry.Message);
        Assert.EndsWith("reason=clientRevision must be greater than zero.", entry.Message);
    }

    [Fact]
    public async Task Sync_logs_scope_mismatch_as_warning()
    {
        var service = new RecordingSyncService();
        var logger = new RecordingLogger<LinklySettlementsController>();
        var controller = CreateController(service, "S001", "POS-01", logger);

        await controller.Sync(CreateRequest() with { StoreCode = "S002" }, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("LinklySettlementSync:DEVICE-SCOPE-FORBIDDEN", entry.EventId.Name);
        Assert.Contains("store=S001 device=POS-01 requestStore=S002 requestDevice=POS-01", entry.Message);
    }

    [Theory]
    [InlineData("IMMUTABLE_FIELDS_CONFLICT", "LinklySettlementSync:IMMUTABLE-FIELDS-CONFLICT")]
    [InlineData("SETTLEMENT_SYNC_CONCURRENT_UPDATE", "LinklySettlementSync:SETTLEMENT-SYNC-CONCURRENT-UPDATE")]
    public void RejectedEvent_name_only_uses_characters_the_center_log_accepts(string code, string expected)
    {
        var name = Hbpos.Api.Logging.RejectionEventIds.Create("LinklySettlementSync", StatusCodes.Status409Conflict, code).Name;

        Assert.Equal(expected, name);
        // 与后端 ApplicationLogService.SafeIdentifierPattern 一致；不匹配时中心日志会把整个 EventId 丢掉。
        Assert.Matches(@"^[A-Za-z0-9][A-Za-z0-9._:/+\-]*$", name);
    }

    private static LinklySettlementsController CreateController(
        ILinklySettlementSyncService service,
        string? storeCode = null,
        string? deviceCode = null,
        ILogger<LinklySettlementsController>? logger = null)
    {
        var controller = new LinklySettlementsController(service, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        if (storeCode is not null && deviceCode is not null)
        {
            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode),
                    new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode)
                ],
                DeviceAuthConstants.Scheme));
        }

        return controller;
    }

    private static LinklySettlementSyncRequest CreateRequest()
    {
        return new LinklySettlementSyncRequest(
            1,
            Guid.NewGuid(),
            "S001",
            "POS-01",
            new DateOnly(2026, 8, 1),
            "LocalIp",
            "Production",
            "session-1",
            "Succeeded",
            "00",
            "APPROVED",
            "TOTAL=10.00",
            ["SETTLEMENT RECEIPT"],
            new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 1, 1, 0, TimeSpan.Zero),
            null,
            null,
            0,
            null,
            1);
    }

    private sealed class RecordingSyncService : ILinklySettlementSyncService
    {
        public LinklySettlementSyncRequest? Request { get; private set; }

        public string? StoreCode { get; private set; }

        public string? DeviceCode { get; private set; }

        public Exception? Exception { get; init; }

        public Task<LinklySettlementSyncResponse> SyncAsync(
            LinklySettlementSyncRequest request,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            Request = request;
            StoreCode = storeCode;
            DeviceCode = deviceCode;
            return Exception is null
                ? Task.FromResult(new LinklySettlementSyncResponse(true, false, request.ClientRevision))
                : Task.FromException<LinklySettlementSyncResponse>(Exception);
        }
    }
}
