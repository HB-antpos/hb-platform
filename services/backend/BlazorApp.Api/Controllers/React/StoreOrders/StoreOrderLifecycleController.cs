using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.Lifecycle;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React.StoreOrders;

[ApiController]
[Route(StoreOrderControllerBase.BaseRoute)]
[Authorize]
public sealed class StoreOrderLifecycleController(
    IStoreOrderLifecycleSlice lifecycle,
    IStoreOrderAccessPolicy accessPolicy,
    ILogger<StoreOrderLifecycleController> logger
) : StoreOrderControllerBase(accessPolicy)
{
    /// <summary>
    /// 完成订单 (FlowStatus -> 2)
    /// </summary>
    [HttpPost("complete/{orderGuid}")]
    public async Task<IActionResult> CompleteOrder(string orderGuid)
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireOrderEditAsync(orderGuid)
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await lifecycle.CompleteOrderAsync(orderGuid);
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            if (result.ErrorCode == "ORDER_STATUS_CONFLICT")
            {
                return Conflict(
                    new
                    {
                        success = false,
                        errorCode = result.ErrorCode,
                        message = result.Message,
                    }
                );
            }

            return BadRequest(
                new
                {
                    success = false,
                    errorCode = result.ErrorCode,
                    message = result.Message,
                }
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CompleteOrder failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 开始配货 (FlowStatus -> 3)
    /// </summary>
    [HttpPost("start-picking/{orderGuid}")]
    public async Task<IActionResult> StartPicking(string orderGuid)
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireStartPickingAsync(orderGuid)
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await lifecycle.StartPickingAsync(orderGuid);
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            if (result.ErrorCode == "ORDER_STATUS_CONFLICT")
            {
                return Conflict(
                    new
                    {
                        success = false,
                        errorCode = result.ErrorCode,
                        message = result.Message,
                    }
                );
            }

            return BadRequest(
                new
                {
                    success = false,
                    errorCode = result.ErrorCode,
                    message = result.Message,
                }
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StartPicking failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 更新订单状态 (Submitted ↔ Completed)
    /// </summary>
    [HttpPost("status")]
    public async Task<IActionResult> UpdateOrderStatus(
        [FromBody] UpdateOrderStatusDto request
    )
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireOrderEditAsync(request.OrderGUID)
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await lifecycle.UpdateOrderStatusAsync(
                request.OrderGUID,
                request.NewStatus
            );
            if (result.Success)
            {
                return Ok(
                    new { success = true, data = result.Data, message = result.Message }
                );
            }

            if (
                result.ErrorCode
                is "PREORDER_SUBMIT_ENDPOINT_REQUIRED" or "ORDER_STATUS_CONFLICT"
            )
            {
                return Conflict(
                    new
                    {
                        success = false,
                        errorCode = result.ErrorCode,
                        message = result.Message,
                    }
                );
            }

            return BadRequest(
                new
                {
                    success = false,
                    errorCode = result.ErrorCode,
                    message = result.Message,
                }
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpdateOrderStatus failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 批量更新订单状态
    /// </summary>
    [HttpPost("batch-status")]
    public async Task<IActionResult> BatchUpdateOrderStatus(
        [FromBody] BatchUpdateOrderStatusDto request
    )
    {
        try
        {
            var forbidden = ForbidIf(
                await AccessPolicy.RequireOrderEditsAsync(request.OrderGUIDs)
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await lifecycle.BatchUpdateOrderStatusAsync(
                request.OrderGUIDs,
                request.NewStatus
            );
            if (result.Success)
            {
                return Ok(
                    new { success = true, data = result.Data, message = result.Message }
                );
            }

            if (
                result.ErrorCode
                is "PREORDER_SUBMIT_ENDPOINT_REQUIRED" or "ORDER_STATUS_CONFLICT"
            )
            {
                return Conflict(
                    new
                    {
                        success = false,
                        errorCode = result.ErrorCode,
                        message = result.Message,
                    }
                );
            }

            return BadRequest(
                new
                {
                    success = false,
                    errorCode = result.ErrorCode,
                    message = result.Message,
                }
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BatchUpdateOrderStatus failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }
}
