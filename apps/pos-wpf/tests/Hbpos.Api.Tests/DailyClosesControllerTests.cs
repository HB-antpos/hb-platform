using System.Reflection;
using System.Security.Claims;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.DailyClose;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Hbpos.Api.Tests;

public sealed class DailyClosesControllerTests
{
    [Fact]
    public void Controller_uses_device_authentication_without_cashier_ticket_or_permission_policy()
    {
        var authorize = Assert.Single(
            typeof(DailyClosesController).GetCustomAttributes<AuthorizeAttribute>());

        // 后台补传时收银员票据可能已过期，所以只认设备授权。
        Assert.Equal(DeviceAuthConstants.Scheme, authorize.AuthenticationSchemes);
        Assert.Null(authorize.Policy);
        // 方法级也不得叠加 Cashier* 策略（那会重新要求收银员票据）。
        Assert.Empty(typeof(DailyClosesController).GetMethod(nameof(DailyClosesController.Sync))!
            .GetCustomAttributes<AuthorizeAttribute>());
    }

    [Fact]
    public void Sync_route_and_body_limit_follow_the_contract()
    {
        var route = Assert.Single(typeof(DailyClosesController).GetCustomAttributes<RouteAttribute>());
        Assert.Equal("api/v1/daily-closes", route.Template);

        var method = typeof(DailyClosesController).GetMethod(nameof(DailyClosesController.Sync))!;
        var post = Assert.Single(method.GetCustomAttributes<HttpPostAttribute>());
        Assert.Equal("sync", post.Template);
        // RequestSizeLimitAttribute 不公开字节数；属性存在 + 常量为 256 KiB 即覆盖「日结 JSON 很小，不必放大到 Linkly 的 1 MiB」这一约束。
        Assert.Single(method.GetCustomAttributes<RequestSizeLimitAttribute>());
        Assert.Equal(256L * 1024, DailyClosesController.MaximumRequestBytes);
    }

    [Fact]
    public async Task Sync_requires_authenticated_device_scope()
    {
        var service = new RecordingSyncService();
        var controller = CreateController(service);

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("DEVICE_AUTH_REQUIRED", CodeOf(unauthorized.Value));
        Assert.Null(service.Request);
    }

    [Theory]
    [InlineData("S001", null)]
    [InlineData(null, "POS-01")]
    [InlineData("S001", " ")]
    [InlineData("", "POS-01")]
    public async Task Sync_rejects_partial_scope_claims_with_401(string? storeCode, string? deviceCode)
    {
        var service = new RecordingSyncService();
        var claims = new List<Claim>();
        if (storeCode is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode));
        }

        if (deviceCode is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode));
        }

        var controller = CreateController(service, claims);

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Sync_rejects_unauthenticated_identity_even_when_claims_are_present()
    {
        var service = new RecordingSyncService();
        var controller = CreateController(service);
        // 没有 authenticationType 的 ClaimsIdentity.IsAuthenticated 为 false。
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, "S001"),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01")
        ]));

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Null(service.Request);
    }

    [Theory]
    [InlineData("S002", "POS-01")]
    [InlineData("S001", "POS-02")]
    [InlineData("s001", "POS-01")]
    [InlineData("S001", "pos-01")]
    public async Task Sync_rejects_body_scope_that_differs_from_claims_with_403(string storeCode, string deviceCode)
    {
        var service = new RecordingSyncService();
        var controller = CreateController(service, "S001", "POS-01");
        var request = CreateRequest() with { StoreCode = storeCode, DeviceCode = deviceCode };

        var result = await controller.Sync(request, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", CodeOf(forbidden.Value));
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Sync_passes_authoritative_claim_scope_to_service_and_returns_its_response()
    {
        var service = new RecordingSyncService
        {
            Response = new DailyCloseSyncResponse(true, false, true)
        };
        var controller = CreateController(service, "S001", "POS-01");
        var request = CreateRequest();

        var result = await controller.Sync(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DailyCloseSyncResponse>(ok.Value);
        Assert.True(response.ReplacedPlaceholder);
        Assert.Same(request, service.Request);
        Assert.Equal("S001", service.StoreCode);
        Assert.Equal("POS-01", service.DeviceCode);
    }

    [Fact]
    public async Task Sync_maps_validation_error_to_400_with_code_and_message()
    {
        var service = new RecordingSyncService
        {
            Exception = new DailyCloseValidationException("INVALID_CASH_COUNTS", "bad counts")
        };
        var controller = CreateController(service, "S001", "POS-01");

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("INVALID_CASH_COUNTS", CodeOf(badRequest.Value));
        Assert.Equal("bad counts", MessageOf(badRequest.Value));
    }

    [Theory]
    [InlineData("DAILY_CLOSE_SCOPE_CONFLICT")]
    [InlineData("DAILY_CLOSE_CONTENT_CONFLICT")]
    [InlineData("DAILY_CLOSE_SYNC_CONCURRENT_UPDATE")]
    public async Task Sync_maps_conflicts_to_409_with_code_and_message(string code)
    {
        var service = new RecordingSyncService
        {
            Exception = new DailyCloseConflictException(code, "conflict")
        };
        var controller = CreateController(service, "S001", "POS-01");

        var result = await controller.Sync(CreateRequest(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(code, CodeOf(conflict.Value));
        Assert.Equal("conflict", MessageOf(conflict.Value));
    }

    [Fact]
    public async Task Sync_does_not_swallow_unexpected_exceptions()
    {
        var service = new RecordingSyncService { Exception = new InvalidOperationException("db down") };
        var controller = CreateController(service, "S001", "POS-01");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.Sync(CreateRequest(), CancellationToken.None));
    }

    private static string? CodeOf(object? value) =>
        value?.GetType().GetProperty("code")?.GetValue(value) as string;

    private static string? MessageOf(object? value) =>
        value?.GetType().GetProperty("message")?.GetValue(value) as string;

    [Fact]
    public async Task Sync_logs_accepted_result_including_placeholder_replacement()
    {
        var service = new RecordingSyncService { Response = new DailyCloseSyncResponse(true, false, true) };
        var logger = new RecordingLogger<DailyClosesController>();
        var controller = CreateController(service, "S001", "POS-01", logger);
        var request = CreateRequest();

        await controller.Sync(request, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith(
            $"Daily close sync accepted result=ReplacedPlaceholder store=S001 device=POS-01 dailyClose={request.DailyCloseGuid}",
            entry.Message);
    }

    [Fact]
    public async Task Sync_logs_conflict_detail_as_warning_without_returning_it_to_the_client()
    {
        var service = new RecordingSyncService
        {
            Exception = new DailyCloseConflictException(
                "DAILY_CLOSE_CONTENT_CONFLICT",
                "The same daily close was uploaded earlier with different content.",
                "field=CountedCashAmount")
        };
        var logger = new RecordingLogger<DailyClosesController>();
        var controller = CreateController(service, "S001", "POS-01", logger);
        var request = CreateRequest();

        var result = await controller.Sync(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(["code", "message"], conflict.Value!.GetType().GetProperties().Select(property => property.Name));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("DailyCloseSync:DAILY-CLOSE-CONTENT-CONFLICT", entry.EventId.Name);
        Assert.Contains($"status=409 code=DAILY_CLOSE_CONTENT_CONFLICT store=S001 device=POS-01 dailyClose={request.DailyCloseGuid}", entry.Message);
        Assert.EndsWith("detail=field=CountedCashAmount", entry.Message);
    }

    [Fact]
    public async Task Sync_logs_validation_and_scope_rejections_as_warnings()
    {
        var logger = new RecordingLogger<DailyClosesController>();
        var invalid = CreateController(
            new RecordingSyncService { Exception = new DailyCloseValidationException("INVALID_TENDERS", "tenders cannot contain null entries.") },
            "S001",
            "POS-01",
            logger);
        var scoped = CreateController(new RecordingSyncService(), "S001", "POS-01", logger);

        await invalid.Sync(CreateRequest(), CancellationToken.None);
        await scoped.Sync(CreateRequest() with { DeviceCode = "POS-99" }, CancellationToken.None);

        Assert.Collection(
            logger.Entries,
            entry =>
            {
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Equal("DailyCloseSync:INVALID-TENDERS", entry.EventId.Name);
                Assert.EndsWith("reason=tenders cannot contain null entries.", entry.Message);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Equal("DailyCloseSync:DEVICE-SCOPE-FORBIDDEN", entry.EventId.Name);
                Assert.Contains("requestDevice=POS-99", entry.Message);
            });
    }

    private static DailyClosesController CreateController(
        IDailyCloseSyncService service,
        string? storeCode = null,
        string? deviceCode = null,
        ILogger<DailyClosesController>? logger = null)
    {
        var claims = new List<Claim>();
        if (storeCode is not null && deviceCode is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode));
            claims.Add(new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode));
        }

        return CreateController(service, claims, logger);
    }

    private static DailyClosesController CreateController(
        IDailyCloseSyncService service,
        IReadOnlyList<Claim> claims,
        ILogger<DailyClosesController>? logger = null)
    {
        var controller = new DailyClosesController(service, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        if (claims.Count > 0)
        {
            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(claims, DeviceAuthConstants.Scheme));
        }

        return controller;
    }

    private static DailyCloseSyncRequest CreateRequest()
    {
        return new DailyCloseSyncRequest(
            SchemaVersion: 1,
            DailyCloseGuid: Guid.NewGuid(),
            StoreCode: "S001",
            DeviceCode: "POS-01",
            ClientKind: "Wpf",
            BusinessDate: new DateOnly(2026, 10, 7),
            PeriodFrom: new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.FromHours(11)),
            PeriodTo: new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.FromHours(11)),
            SavedAt: new DateTimeOffset(2026, 10, 7, 20, 0, 0, TimeSpan.FromHours(11)),
            CashierId: "C001",
            CashierName: "Alice",
            AppVersion: "1.0.47",
            OrderCount: 1,
            ReturnQuantity: 0m,
            RefundAmount: 0m,
            Tenders:
            [
                new DailyCloseTenderSync("Cash", 10m, 0m, 10m),
                new DailyCloseTenderSync("Card", 0m, 0m, 0m),
                new DailyCloseTenderSync("Voucher", 0m, 0m, 0m)
            ],
            CashCounts: DailyCloseContractConstants.DenominationCents
                .Select(denomination => new DailyCloseCashCountSync(denomination, denomination == 1000 ? 1 : 0))
                .ToArray(),
            NoteSubtotal: 10m,
            CoinSubtotal: 0m,
            CountedCashAmount: 10m,
            CashDifference: 0m);
    }

    private sealed class RecordingSyncService : IDailyCloseSyncService
    {
        public DailyCloseSyncRequest? Request { get; private set; }

        public string? StoreCode { get; private set; }

        public string? DeviceCode { get; private set; }

        public Exception? Exception { get; init; }

        public DailyCloseSyncResponse Response { get; init; } = new(true, false, false);

        public Task<DailyCloseSyncResponse> SyncAsync(
            DailyCloseSyncRequest request,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            Request = request;
            StoreCode = storeCode;
            DeviceCode = deviceCode;
            return Exception is null
                ? Task.FromResult(Response)
                : Task.FromException<DailyCloseSyncResponse>(Exception);
        }
    }
}
