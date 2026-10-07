using Hbpos.Api.Auth;
using Hbpos.Api.Logging;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
public sealed class OrdersController(
    IOrderSyncService orderSyncService,
    IOrderHistoryService orderHistoryService,
    IOrderReturnService orderReturnService,
    ILogger<OrdersController>? logger = null) : ControllerBase
{
    [Authorize(Policy = CashierAuthorizationPolicies.OrderSync)]
    [HttpPost("sync")]
    public async Task<ActionResult<ApiResult<OrderSyncResponse>>> Sync(
        [FromBody] OrderSyncRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log(
            $"sync request start orderGuid={request.OrderGuid:D} store={request.StoreCode} device={request.DeviceCode} " +
            $"lines={request.Lines.Count} payments={request.Payments.Count} actualAmount={request.ActualAmount}");
        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            LogSyncRejected(StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN", request, "device-scope-forbidden", stopwatch);
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<OrderSyncResponse>("Device is not authorized for this store.");
        }

        if (request.Lines.Count == 0)
        {
            LogSyncRejected(StatusCodes.Status400BadRequest, "ORDER_LINES_REQUIRED", request, "missing-lines", stopwatch);
            return BadRequest(ApiResult<OrderSyncResponse>.Fail("ORDER_LINES_REQUIRED", "订单明细不能为空"));
        }

        if (request.ActualAmount != 0m && request.Payments.Count == 0)
        {
            LogSyncRejected(StatusCodes.Status400BadRequest, "ORDER_PAYMENTS_REQUIRED", request, "missing-payments", stopwatch);
            return BadRequest(ApiResult<OrderSyncResponse>.Fail("ORDER_PAYMENTS_REQUIRED", "订单付款不能为空"));
        }

        try
        {
            var response = await orderSyncService.SyncAsync(request, cancellationToken);
            Log(
                $"sync request completed orderGuid={request.OrderGuid:D} accepted={response.Accepted} " +
                $"alreadySynced={response.AlreadySynced} message={response.Message} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return Ok(ApiResult<OrderSyncResponse>.Ok(response));
        }
        catch (OrderSyncQuantityUnsupportedException ex)
        {
            LogSyncRejected(StatusCodes.Status400BadRequest, "ORDER_SYNC_QUANTITY_UNSUPPORTED", request, ex.Message, stopwatch);
            return BadRequest(ApiResult<OrderSyncResponse>.Fail("ORDER_SYNC_QUANTITY_UNSUPPORTED", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            LogSyncRejected(StatusCodes.Status400BadRequest, "ORDER_SYNC_INVALID", request, $"{ex.GetType().Name}: {ex.Message}", stopwatch);
            return BadRequest(ApiResult<OrderSyncResponse>.Fail("ORDER_SYNC_INVALID", ex.Message));
        }
        catch (Exception ex) when (LogSyncFailed(ex, request, stopwatch, cancellationToken))
        {
            // 不会进入：过滤器总是返回 false，只在异常继续上抛前补一条带订单号的日志。
            throw;
        }
    }

    private void Log(string message)
    {
        logger?.LogInformation("OrderSyncController {Message}", message);
    }

    /// <summary>
    /// 订单被拒绝时客户端不会自动成功，必须进中心日志（Warning）；线上业务拒绝极少（2026-07~10 日志里 0 次），不会成噪音。
    /// </summary>
    private void LogSyncRejected(int statusCode, string code, OrderSyncRequest request, string reason, Stopwatch stopwatch)
    {
        logger?.LogWarning(
            RejectionEventIds.Create("OrderSync", statusCode, code),
            "OrderSyncController sync request rejected status={StatusCode} code={Code} orderGuid={OrderGuid} store={StoreCode} device={DeviceCode} reason={Reason} elapsedMs={ElapsedMs}",
            statusCode,
            code,
            request.OrderGuid,
            request.StoreCode,
            request.DeviceCode,
            reason,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// 意外异常原本只由 Kestrel 记一条不带订单号的 Error（2026-08-17 一笔订单重试 1094 次都查不到原因），
    /// 这里在异常继续上抛前补一条带订单号的 Warning；客户端主动断开属于正常取消，不记。
    /// </summary>
    private bool LogSyncFailed(Exception exception, OrderSyncRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        logger?.LogWarning(
            RejectionEventIds.Create("OrderSync", StatusCodes.Status500InternalServerError, "UNHANDLED"),
            "OrderSyncController sync request failed orderGuid={OrderGuid} store={StoreCode} device={DeviceCode} error={ErrorType} message={ErrorMessage} elapsedMs={ElapsedMs}",
            request.OrderGuid,
            request.StoreCode,
            request.DeviceCode,
            exception.GetType().Name,
            exception.Message,
            stopwatch.ElapsedMilliseconds);
        return false;
    }

    [Authorize(Policy = CashierAuthorizationPolicies.History)]
    [HttpGet("history")]
    public async Task<ActionResult<ApiResult<OrderHistoryQueryResponse>>> History(
        [FromQuery] string storeCode,
        [FromQuery] string? deviceCode,
        [FromQuery] DateTimeOffset? soldFrom,
        [FromQuery] DateTimeOffset? soldTo,
        [FromQuery] string? keyword,
        [FromQuery] int take,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<OrderHistoryQueryResponse>.Fail("STORE_CODE_REQUIRED", "Store code is required."));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<OrderHistoryQueryResponse>("Device is not authorized for this store.");
        }

        var response = await orderHistoryService.QueryAsync(
            new OrderHistoryQueryRequest(storeCode, deviceCode, soldFrom, soldTo, keyword, take <= 0 ? 100 : take),
            cancellationToken);
        return Ok(ApiResult<OrderHistoryQueryResponse>.Ok(response));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.History)]
    [HttpGet("history/{orderGuid:guid}")]
    public async Task<ActionResult<ApiResult<OrderHistoryDetailsDto?>>> HistoryDetails(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var details = await orderHistoryService.GetDetailsAsync(orderGuid, cancellationToken);
        if (details is null)
        {
            return Ok(ApiResult<OrderHistoryDetailsDto?>.Ok(null));
        }

        if (!this.IsDeviceScopeAllowed(details.StoreCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<OrderHistoryDetailsDto?>("Device is not authorized for this store.");
        }

        return Ok(ApiResult<OrderHistoryDetailsDto?>.Ok(details));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.Returns)]
    [HttpGet("history/{orderGuid:guid}/return-context")]
    public async Task<ActionResult<ApiResult<OrderReturnContextDto?>>> ReturnContext(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var context = await orderReturnService.GetReturnContextAsync(orderGuid, cancellationToken);
        if (context is null)
        {
            return Ok(ApiResult<OrderReturnContextDto?>.Ok(null));
        }

        if (!this.IsDeviceScopeAllowed(context.Order.StoreCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<OrderReturnContextDto?>("Device is not authorized for this store.");
        }

        return Ok(ApiResult<OrderReturnContextDto?>.Ok(context));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.Returns)]
    [HttpPost("returns")]
    public async Task<ActionResult<ApiResult<OrderReturnRecordCreateResponse>>> CreateReturns(
        [FromBody] OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            LogReturnRejected(StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN", request, "device-scope-forbidden");
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<OrderReturnRecordCreateResponse>("Device is not authorized for this store.");
        }

        if (request.Lines.Count == 0)
        {
            LogReturnRejected(StatusCodes.Status400BadRequest, "RETURN_LINES_REQUIRED", request, "missing-lines");
            return BadRequest(ApiResult<OrderReturnRecordCreateResponse>.Fail("RETURN_LINES_REQUIRED", "退货明细不能为空"));
        }

        try
        {
            var response = await orderReturnService.CreateRecordsAsync(request, cancellationToken);
            return Ok(ApiResult<OrderReturnRecordCreateResponse>.Ok(response));
        }
        catch (InvalidOperationException ex)
        {
            LogReturnRejected(StatusCodes.Status400BadRequest, "RETURN_RECORD_INVALID", request, ex.Message);
            return BadRequest(ApiResult<OrderReturnRecordCreateResponse>.Fail("RETURN_RECORD_INVALID", ex.Message));
        }
    }

    private void LogReturnRejected(int statusCode, string code, OrderReturnRecordCreateRequest request, string reason)
    {
        logger?.LogWarning(
            RejectionEventIds.Create("OrderReturn", statusCode, code),
            "OrderReturns create rejected status={StatusCode} code={Code} returnOrderGuid={ReturnOrderGuid} store={StoreCode} device={DeviceCode} lines={LineCount} reason={Reason}",
            statusCode,
            code,
            request.ReturnOrderGuid,
            request.StoreCode,
            request.DeviceCode,
            request.Lines.Count,
            reason);
    }
}
