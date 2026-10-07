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

namespace Hbpos.Api.Tests;

public sealed class OrdersControllerTests
{
    [Fact]
    public async Task Sync_AllowsZeroActualAmountWithoutPayments()
    {
        var service = new FakeOrderSyncService();
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService());
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
        var controller = new OrdersController(new FakeOrderSyncService(), new FakeOrderHistoryService(), new FakeOrderReturnService());
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
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService());
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
        var controller = new OrdersController(new FakeOrderSyncService(), new FakeOrderHistoryService(), new FakeOrderReturnService(), logger);
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
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService(), logger);
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
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService(), logger);
        SetAuthenticatedDevice(controller, "S01", "POS01");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.Sync(CreateRequest(actualAmount: 0m, payments: []), cancellation.Token));

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
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
        public Task<OrderHistoryQueryResponse> QueryAsync(OrderHistoryQueryRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OrderHistoryDetailsDto?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeOrderReturnService : IOrderReturnService
    {
        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OrderReturnRecordCreateResponse> CreateRecordsAsync(OrderReturnRecordCreateRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
