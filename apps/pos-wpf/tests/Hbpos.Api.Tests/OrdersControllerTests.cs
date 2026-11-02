using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Orders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class OrdersControllerTests
{
    [Fact]
    public async Task Sync_AllowsZeroActualAmountWithoutPayments()
    {
        var service = new FakeOrderSyncService();
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService(), CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");
        var request = CreateRequest(actualAmount: 0m, payments: []);

        var result = await controller.Sync(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<OrderSyncResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.NotNull(service.LastRequest);
        Assert.Empty(service.LastRequest!.Payments);
    }

    [Fact]
    public async Task Sync_RejectsNonZeroActualAmountWithoutPayments()
    {
        var controller = new OrdersController(new FakeOrderSyncService(), new FakeOrderHistoryService(), new FakeOrderReturnService(), CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");
        var request = CreateRequest(actualAmount: 1m, payments: []);

        var result = await controller.Sync(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<OrderSyncResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("ORDER_PAYMENTS_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Sync_ReturnsDedicatedQuantityCodeForHistoricalFractionalOrder()
    {
        var service = new FakeOrderSyncService { RejectQuantity = true };
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService(), CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");

        var result = await controller.Sync(CreateRequest(actualAmount: 0m, payments: []), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<OrderSyncResponse>>(badRequest.Value);
        Assert.Equal("ORDER_SYNC_QUANTITY_UNSUPPORTED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Sync_logs_business_rejection_as_warning_with_rejection_code()
    {
        var logger = new RecordingLogger<OrdersController>();
        var controller = new OrdersController(new FakeOrderSyncService(), new FakeOrderHistoryService(), new FakeOrderReturnService(), CreateWindowService(), logger);
        SetAuthenticatedDevice(controller, "S01", "POS01");
        var request = CreateRequest(actualAmount: 1m, payments: []);

        await controller.Sync(request, CancellationToken.None);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("OrderSync:ORDER-PAYMENTS-REQUIRED", warning.EventId.Name);
        Assert.StartsWith(
            $"OrderSyncController sync request rejected status=400 code=ORDER_PAYMENTS_REQUIRED orderGuid={request.OrderGuid} store=S01 device=POS01 reason=missing-payments",
            warning.Message);
    }

    [Fact]
    public async Task Sync_logs_unexpected_failure_with_order_guid_and_still_throws()
    {
        var logger = new RecordingLogger<OrdersController>();
        var service = new FakeOrderSyncService { Exception = new TimeoutException("db timeout") };
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService(), CreateWindowService(), logger);
        SetAuthenticatedDevice(controller, "S01", "POS01");
        var request = CreateRequest(actualAmount: 0m, payments: []);

        await Assert.ThrowsAsync<TimeoutException>(() => controller.Sync(request, CancellationToken.None));

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("OrderSync:UNHANDLED", warning.EventId.Name);
        Assert.Contains($"orderGuid={request.OrderGuid} store=S01 device=POS01 error=TimeoutException message=db timeout", warning.Message);
    }

    [Fact]
    public async Task Sync_does_not_log_a_failure_when_the_client_cancelled_the_request()
    {
        var logger = new RecordingLogger<OrdersController>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var service = new FakeOrderSyncService { Exception = new OperationCanceledException(cancellation.Token) };
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService(), CreateWindowService(), logger);
        SetAuthenticatedDevice(controller, "S01", "POS01");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.Sync(CreateRequest(actualAmount: 0m, payments: []), cancellation.Token));

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    // 门店在悉尼：2026-11-02 01:00Z = 悉尼 11-02 12:00（夏令时 +11），最早可查 2025-11-02。
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-11-02T01:00:00Z");

    [Fact]
    public async Task History_rejects_range_longer_than_one_month()
    {
        var history = new FakeOrderHistoryService();
        var controller = new OrdersController(new FakeOrderSyncService(), history, new FakeOrderReturnService(), CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");

        var result = await controller.History(
            "S01",
            null,
            DateTimeOffset.Parse("2026-10-02T00:00:00+10:00"),
            DateTimeOffset.Parse("2026-11-02T23:59:59+11:00"),
            null,
            50,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<OrderHistoryQueryResponse>>(badRequest.Value);
        Assert.Equal(OrderQueryWindowRules.ErrorRangeTooLong, apiResult.ErrorCode);
        Assert.Null(history.LastRequest);
    }

    [Fact]
    public async Task History_rejects_start_date_older_than_one_year()
    {
        var history = new FakeOrderHistoryService();
        var controller = new OrdersController(new FakeOrderSyncService(), history, new FakeOrderReturnService(), CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");

        var result = await controller.History(
            "S01",
            null,
            DateTimeOffset.Parse("2025-11-01T00:00:00+11:00"),
            DateTimeOffset.Parse("2025-11-20T23:59:59+11:00"),
            null,
            50,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<OrderHistoryQueryResponse>>(badRequest.Value);
        Assert.Equal(OrderQueryWindowRules.ErrorTooOld, apiResult.ErrorCode);
        Assert.Contains("2025-11-02", apiResult.Message);
        Assert.Null(history.LastRequest);
    }

    [Fact]
    public async Task History_product_keyword_without_dates_defaults_to_the_last_month()
    {
        var history = new FakeOrderHistoryService();
        var controller = new OrdersController(new FakeOrderSyncService(), history, new FakeOrderReturnService(), CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");

        var result = await controller.History("S01", null, null, null, "9311192735139", 1, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        // 悉尼夏令时 10-04 才开始，10-03 当天是 +10:00。
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T00:00:00+10:00"), history.LastRequest!.SoldFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T00:00:00+11:00").AddDays(1).AddTicks(-1), history.LastRequest.SoldTo);
    }

    [Theory]
    [InlineData("DB596F11")]
    [InlineData("6a3c4dfd-ab7b-4d95-84db-024edb596f11")]
    public async Task History_order_locator_without_dates_only_gets_the_one_year_floor(string keyword)
    {
        var history = new FakeOrderHistoryService();
        var controller = new OrdersController(new FakeOrderSyncService(), history, new FakeOrderReturnService(), CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");

        var result = await controller.History("S01", null, null, null, keyword, 20, CancellationToken.None);

        // 按单号查小票不受 1 个月跨度限制：近 12 个月有 0.8% 的退货原单在 1 个月以前。
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(DateTimeOffset.Parse("2025-11-02T00:00:00+11:00"), history.LastRequest!.SoldFrom);
        Assert.Null(history.LastRequest.SoldTo);
    }

    [Fact]
    public async Task ReturnContext_rejects_original_order_older_than_one_year()
    {
        var returns = new FakeOrderReturnService { Context = CreateReturnContext(DateTimeOffset.Parse("2025-11-01T18:30:00+11:00")) };
        var controller = new OrdersController(new FakeOrderSyncService(), new FakeOrderHistoryService(), returns, CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");

        var result = await controller.ReturnContext(returns.Context!.Order.OrderGuid, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<OrderReturnContextDto?>>(badRequest.Value);
        Assert.Equal(OrderQueryWindowRules.ErrorTooOldForReturn, apiResult.ErrorCode);
        Assert.Contains("2025-11-01", apiResult.Message);
    }

    [Fact]
    public async Task ReturnContext_allows_original_order_sold_exactly_one_year_ago()
    {
        var returns = new FakeOrderReturnService { Context = CreateReturnContext(DateTimeOffset.Parse("2025-11-02T09:00:00+11:00")) };
        var controller = new OrdersController(new FakeOrderSyncService(), new FakeOrderHistoryService(), returns, CreateWindowService());
        SetAuthenticatedDevice(controller, "S01", "POS01");

        var result = await controller.ReturnContext(returns.Context!.Order.OrderGuid, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(returns.Context, Assert.IsType<ApiResult<OrderReturnContextDto?>>(ok.Value).Data);
    }

    private static OrderQueryWindowService CreateWindowService() => new(
        new StubStoreTimeZoneResolver(TestStoreTimeZones.Sydney),
        Options.Create(new OrderQueryWindowOptions()),
        new FixedTimeProvider(FixedNow));

    private static OrderReturnContextDto CreateReturnContext(DateTimeOffset soldAt) => new(
        new OrderHistoryDetailsDto(Guid.NewGuid(), "S01", "POS01", "Cashier", soldAt, 10m, 0m, 10m, [], []),
        [],
        [],
        []);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static OrderSyncRequest CreateRequest(decimal actualAmount, IReadOnlyList<PaymentSyncDto> payments)
    {
        return new OrderSyncRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            10m,
            0m,
            actualAmount,
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    "P01",
                    "SOURCE-GUID-01",
                    "Apple",
                    "BAR01",
                    1m,
                    10m,
                    0m,
                    10m,
                    PriceSourceKind.StoreRetailPrice)
            ],
            payments);
    }

    private static void SetAuthenticatedDevice(
        ControllerBase controller,
        string storeCode,
        string deviceCode)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode),
            new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
        ], DeviceAuthConstants.Scheme);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }

    private sealed class FakeOrderSyncService : IOrderSyncService
    {
        public OrderSyncRequest? LastRequest { get; private set; }
        public bool RejectQuantity { get; set; }
        public Exception? Exception { get; set; }

        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (RejectQuantity) throw new OrderSyncQuantityUnsupportedException();
            if (Exception is not null) throw Exception;
            return Task.FromResult(new OrderSyncResponse(request.OrderGuid, true, false, "Synced"));
        }
    }

    private sealed class FakeOrderHistoryService : IOrderHistoryService
    {
        public OrderHistoryQueryRequest? LastRequest { get; private set; }

        public Task<OrderHistoryQueryResponse> QueryAsync(OrderHistoryQueryRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new OrderHistoryQueryResponse([]));
        }

        public Task<OrderHistoryDetailsDto?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeOrderReturnService : IOrderReturnService
    {
        public OrderReturnContextDto? Context { get; init; }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            return Context is null ? throw new NotSupportedException() : Task.FromResult<OrderReturnContextDto?>(Context);
        }

        public Task<OrderReturnRecordCreateResponse> CreateRecordsAsync(OrderReturnRecordCreateRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
