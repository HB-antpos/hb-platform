using System.Diagnostics;
using BlazorApp.Api.Features.StoreOrders.Cart;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React.StoreOrders;

[ApiController]
[Route(StoreOrderControllerBase.BaseRoute)]
[Authorize]
public sealed class StoreOrderCartController : StoreOrderControllerBase
{
    private const string ScanTraceHeaderName = "X-Scan-Trace-Id";
    private const string CartFlowCheckType = "cart-flow";
    private const string ScanOrderFlowCheckType = "scan-order-flow";

    private readonly IStoreOrderCartSlice _cartSlice;
    private readonly IStoreOrderPlacementSlice _orderPlacementSlice;
    private readonly ILogger<StoreOrderCartController> _logger;

    public StoreOrderCartController(
        IStoreOrderCartSlice cartSlice,
        IStoreOrderPlacementSlice orderPlacementSlice,
        IStoreOrderAccessPolicy accessPolicy,
        ILogger<StoreOrderCartController> logger
    ) : base(accessPolicy)
    {
        _cartSlice = cartSlice;
        _orderPlacementSlice = orderPlacementSlice;
        _logger = logger;
    }

    /// <summary>
    /// 扫码查询并加购：单命中时一次请求完成加购，0/多命中只返回候选。
    /// </summary>
    [HttpPost("cart/scan-lookup-add")]
    public async Task<IActionResult> ScanLookupAndAddToCart(
        [FromBody] StoreOrderScanLookupAddRequestDto request
    )
    {
        var totalSw = Stopwatch.StartNew();
        var traceId = GetScanTraceId();
        try
        {
            var permissionSw = Stopwatch.StartNew();
            var forbidden = ForbidIf(
                await AccessPolicy.RequireCartWriteAsync(
                    request.StoreCode,
                    ScanOrderFlowCheckType
                )
            );
            permissionSw.Stop();
            if (forbidden != null)
            {
                _logger.LogInformation(
                    "[shop-scan-perf] traceId={TraceId} stage=scan.lookup-add.controller.forbidden storeCode={StoreCode} barcodeTail={BarcodeTail} barcodeLength={BarcodeLength} permissionMs={PermissionMs} totalMs={TotalMs}",
                    traceId,
                    request.StoreCode,
                    GetBarcodeTail(request.Barcode),
                    GetBarcodeLength(request.Barcode),
                    permissionSw.ElapsedMilliseconds,
                    totalSw.ElapsedMilliseconds
                );
                return forbidden;
            }

            var serviceSw = Stopwatch.StartNew();
            var result = await _cartSlice.ScanLookupAndAddToCartMutationAsync(request);
            serviceSw.Stop();
            _logger.LogInformation(
                "[shop-scan-perf] traceId={TraceId} stage=scan.lookup-add.controller.done storeCode={StoreCode} barcodeTail={BarcodeTail} barcodeLength={BarcodeLength} success={Success} itemCount={ItemCount} added={Added} permissionMs={PermissionMs} serviceMs={ServiceMs} totalMs={TotalMs}",
                traceId,
                request.StoreCode,
                GetBarcodeTail(request.Barcode),
                GetBarcodeLength(request.Barcode),
                result.Success,
                result.Data?.Items?.Count ?? 0,
                result.Data?.Added ?? false,
                permissionSw.ElapsedMilliseconds,
                serviceSw.ElapsedMilliseconds,
                totalSw.ElapsedMilliseconds
            );

            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[shop-scan-perf] traceId={TraceId} stage=scan.lookup-add.controller.error storeCode={StoreCode} barcodeTail={BarcodeTail} barcodeLength={BarcodeLength} totalMs={TotalMs}",
                traceId,
                request.StoreCode,
                GetBarcodeTail(request.Barcode),
                GetBarcodeLength(request.Barcode),
                totalSw.ElapsedMilliseconds
            );
            _logger.LogError(ex, "ScanLookupAndAddToCart failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 获取分店当前的购物车
    /// </summary>
    [HttpGet("cart/{storeCode}")]
    public async Task<IActionResult> GetActiveCart(string storeCode)
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireCartReadAsync(storeCode, CartFlowCheckType)
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await _cartSlice.GetActiveCartAsync(storeCode);
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }
            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetActiveCart failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 获取分店当前购物车的轻量汇总
    /// </summary>
    [HttpGet("cart/{storeCode}/summary")]
    public async Task<IActionResult> GetActiveCartSummary(string storeCode)
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireCartReadAsync(storeCode, CartFlowCheckType)
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await _cartSlice.GetActiveCartSummaryAsync(storeCode);
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetActiveCartSummary failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 添加到购物车
    /// </summary>
    [HttpPost("cart/add")]
    [HttpPost("cart/scan-add")]
    public async Task<IActionResult> AddToCart([FromBody] AddToCartRequestDto request)
    {
        var totalSw = Stopwatch.StartNew();
        var traceId = GetScanTraceId();
        try
        {
            var permissionSw = Stopwatch.StartNew();
            var forbidden = ForbidIf(
                await AccessPolicy.RequireCartWriteAsync(
                    request.StoreCode,
                    ScanOrderFlowCheckType
                )
            );
            permissionSw.Stop();
            if (forbidden != null)
            {
                _logger.LogInformation(
                    "[shop-scan-perf] traceId={TraceId} stage=cart.add.controller.forbidden storeCode={StoreCode} productCode={ProductCode} permissionMs={PermissionMs} totalMs={TotalMs}",
                    traceId,
                    request.StoreCode,
                    request.ProductCode,
                    permissionSw.ElapsedMilliseconds,
                    totalSw.ElapsedMilliseconds
                );
                return forbidden;
            }

            var serviceSw = Stopwatch.StartNew();
            if (IsScanCartMutationRoute())
            {
                var mutationResult = await _cartSlice.AddToCartMutationAsync(request);
                serviceSw.Stop();
                _logger.LogInformation(
                    "[shop-scan-perf] traceId={TraceId} stage=cart.add.controller.done storeCode={StoreCode} productCode={ProductCode} quantity={Quantity} success={Success} totalQuantity={TotalQuantity} permissionMs={PermissionMs} serviceMs={ServiceMs} totalMs={TotalMs}",
                    traceId,
                    request.StoreCode,
                    request.ProductCode,
                    request.Quantity,
                    mutationResult.Success,
                    mutationResult.Data?.Summary.TotalQuantity ?? 0,
                    permissionSw.ElapsedMilliseconds,
                    serviceSw.ElapsedMilliseconds,
                    totalSw.ElapsedMilliseconds
                );
                if (mutationResult.Success)
                {
                    return Ok(new { success = true, data = mutationResult.Data });
                }
                return BadRequest(
                    new { success = false, message = mutationResult.Message }
                );
            }

            var result = await _cartSlice.AddToCartAsync(request);
            serviceSw.Stop();
            _logger.LogInformation(
                "[shop-scan-perf] traceId={TraceId} stage=cart.add.controller.done storeCode={StoreCode} productCode={ProductCode} quantity={Quantity} success={Success} totalQuantity={TotalQuantity} permissionMs={PermissionMs} serviceMs={ServiceMs} totalMs={TotalMs}",
                traceId,
                request.StoreCode,
                request.ProductCode,
                request.Quantity,
                result.Success,
                result.Data?.TotalQuantity ?? 0,
                permissionSw.ElapsedMilliseconds,
                serviceSw.ElapsedMilliseconds,
                totalSw.ElapsedMilliseconds
            );
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }
            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[shop-scan-perf] traceId={TraceId} stage=cart.add.controller.error storeCode={StoreCode} productCode={ProductCode} quantity={Quantity} totalMs={TotalMs}",
                traceId,
                request.StoreCode,
                request.ProductCode,
                request.Quantity,
                totalSw.ElapsedMilliseconds
            );
            _logger.LogError(ex, "AddToCart failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 更新购物车项数量 (覆盖)
    /// </summary>
    [HttpPost("cart/update")]
    [HttpPost("cart/scan-update")]
    public async Task<IActionResult> UpdateCartItem(
        [FromBody] AddToCartRequestDto request
    )
    {
        var totalSw = Stopwatch.StartNew();
        var traceId = GetScanTraceId();
        try
        {
            var permissionSw = Stopwatch.StartNew();
            var forbidden = ForbidIf(
                await AccessPolicy.RequireCartWriteAsync(
                    request.StoreCode,
                    ScanOrderFlowCheckType
                )
            );
            permissionSw.Stop();
            if (forbidden != null)
            {
                _logger.LogInformation(
                    "[shop-scan-perf] traceId={TraceId} stage=cart.update.controller.forbidden storeCode={StoreCode} productCode={ProductCode} permissionMs={PermissionMs} totalMs={TotalMs}",
                    traceId,
                    request.StoreCode,
                    request.ProductCode,
                    permissionSw.ElapsedMilliseconds,
                    totalSw.ElapsedMilliseconds
                );
                return forbidden;
            }

            var serviceSw = Stopwatch.StartNew();
            if (IsScanCartMutationRoute())
            {
                var mutationResult = await _cartSlice.UpdateCartItemMutationAsync(request);
                serviceSw.Stop();
                _logger.LogInformation(
                    "[shop-scan-perf] traceId={TraceId} stage=cart.update.controller.done storeCode={StoreCode} productCode={ProductCode} quantity={Quantity} success={Success} totalQuantity={TotalQuantity} permissionMs={PermissionMs} serviceMs={ServiceMs} totalMs={TotalMs}",
                    traceId,
                    request.StoreCode,
                    request.ProductCode,
                    request.Quantity,
                    mutationResult.Success,
                    mutationResult.Data?.Summary.TotalQuantity ?? 0,
                    permissionSw.ElapsedMilliseconds,
                    serviceSw.ElapsedMilliseconds,
                    totalSw.ElapsedMilliseconds
                );
                if (mutationResult.Success)
                {
                    return Ok(new { success = true, data = mutationResult.Data });
                }
                return BadRequest(
                    new { success = false, message = mutationResult.Message }
                );
            }

            var result = await _cartSlice.UpdateCartItemAsync(request);
            serviceSw.Stop();
            _logger.LogInformation(
                "[shop-scan-perf] traceId={TraceId} stage=cart.update.controller.done storeCode={StoreCode} productCode={ProductCode} quantity={Quantity} success={Success} totalQuantity={TotalQuantity} permissionMs={PermissionMs} serviceMs={ServiceMs} totalMs={TotalMs}",
                traceId,
                request.StoreCode,
                request.ProductCode,
                request.Quantity,
                result.Success,
                result.Data?.TotalQuantity ?? 0,
                permissionSw.ElapsedMilliseconds,
                serviceSw.ElapsedMilliseconds,
                totalSw.ElapsedMilliseconds
            );
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }
            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[shop-scan-perf] traceId={TraceId} stage=cart.update.controller.error storeCode={StoreCode} productCode={ProductCode} quantity={Quantity} totalMs={TotalMs}",
                traceId,
                request.StoreCode,
                request.ProductCode,
                request.Quantity,
                totalSw.ElapsedMilliseconds
            );
            _logger.LogError(ex, "UpdateCartItem failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 移除购物车项
    /// </summary>
    [HttpPost("cart/remove")]
    public async Task<IActionResult> RemoveFromCart(
        [FromBody] RemoveFromCartRequestDto request
    )
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireCartWriteAsync(
                    request.StoreCode,
                    CartFlowCheckType
                )
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await _cartSlice.RemoveFromCartAsync(request);
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }
            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveFromCart failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 清空购物车
    /// </summary>
    [HttpPost("cart/clear")]
    public async Task<IActionResult> ClearCart([FromBody] ClearCartRequestDto request)
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireCartWriteAsync(
                    request.StoreCode,
                    CartFlowCheckType
                )
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await _cartSlice.ClearCartAsync(request.StoreCode);
            if (result.Success)
            {
                return Ok(result);
            }
            return BadRequest(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClearCart failed");
            return StatusCode(
                500,
                new ApiResponse<object>
                {
                    Success = false,
                    Message = "Internal server error",
                }
            );
        }
    }

    /// <summary>
    /// 提交订单
    /// </summary>
    [HttpPost("submit")]
    public async Task<IActionResult> SubmitOrder(
        [FromBody] SubmitStoreOrderRequestDto request
    )
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireCartWriteAsync(
                    request.StoreCode,
                    CartFlowCheckType
                )
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await _orderPlacementSlice.SubmitOrderAsync(request);
            if (result.Success)
            {
                // data 里带保留在购物车的行；message 是同一内容的中文说明，供旧客户端直接展示。
                return Ok(new { success = true, message = result.Message, data = result.Data });
            }
            var atomicGateError = MapPreorderGateServiceError(
                result.ErrorCode,
                result.Message,
                result.Details
            );
            if (atomicGateError != null)
            {
                return atomicGateError;
            }
            if (result.ErrorCode == StoreOrderSupplyGuard.PausedErrorCode)
            {
                // 购物车里没有可提交的行：带上错误码、受影响货号与各行的供货计划，前端据此标出是哪几行。
                return BadRequest(
                    new
                    {
                        success = false,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                        details = result.Details,
                        data = result.Data,
                    }
                );
            }
            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubmitOrder failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    private string GetScanTraceId()
    {
        // 前端扫码链路透传 traceId，缺失时使用 ASP.NET TraceIdentifier 兜底。
        return GetExplicitScanTraceId() ?? HttpContext.TraceIdentifier;
    }

    private string? GetExplicitScanTraceId()
    {
        var traceId = Request.Headers[ScanTraceHeaderName].FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(traceId) ? null : traceId;
    }

    private static string GetBarcodeTail(string? barcode)
    {
        var trimmed = barcode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "empty";
        }

        return trimmed.Length <= 6 ? trimmed : trimmed[^6..];
    }

    private static int GetBarcodeLength(string? barcode)
    {
        return barcode?.Trim().Length ?? 0;
    }
}
