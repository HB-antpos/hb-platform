using BlazorApp.Api.Filters;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.ImportPriceVariance;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React.StoreOrders;

/// <summary>
/// 分店订货首次货柜进货价基准差异接口。
/// </summary>
[ApiController]
[Route(StoreOrderControllerBase.BaseRoute)]
[Authorize]
[MaskContainerNumbers]
public sealed class StoreOrderImportPriceVarianceController(
    IStoreOrderImportPriceVarianceSlice importPriceVarianceSlice,
    IStoreOrderAccessPolicy accessPolicy,
    ILogger<StoreOrderImportPriceVarianceController> logger
) : StoreOrderControllerBase(accessPolicy)
{
    /// <summary>
    /// 获取首次货柜进货价基准差异统计。
    /// </summary>
    [HttpPost("import-price-variance")]
    public async Task<IActionResult> GetImportPriceVariance(
        [FromBody] StoreOrderImportPriceVarianceQueryDto query
    )
    {
        try
        {
            query ??= new StoreOrderImportPriceVarianceQueryDto();

            // 首柜价差异统计是仓库管理员报表，不能用 WarehouseStaff 的只读订货权限直接查询。
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await importPriceVarianceSlice.GetImportPriceVarianceAsync(query);
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetImportPriceVariance failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 获取首次货柜进货价基准差异单商品订单明细。
    /// </summary>
    [HttpPost("import-price-variance/details")]
    public async Task<IActionResult> GetImportPriceVarianceDetails(
        [FromBody] StoreOrderImportPriceVarianceDetailQueryDto query
    )
    {
        try
        {
            query ??= new StoreOrderImportPriceVarianceDetailQueryDto();

            // 明细同样会暴露跨分店基准差异，必须和统计页保持仓库管理员权限一致。
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await importPriceVarianceSlice.GetImportPriceVarianceDetailsAsync(
                query
            );
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetImportPriceVarianceDetails failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 更新首次货柜价差异统计页展示的仓库当前国内价格。
    /// </summary>
    [HttpPost("import-price-variance/domestic-price")]
    public async Task<IActionResult> UpdateImportPriceVarianceDomesticPrice(
        [FromBody] StoreOrderImportPriceVarianceDomesticPriceUpdateDto request
    )
    {
        try
        {
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await importPriceVarianceSlice.UpdateImportPriceVarianceDomesticPriceAsync(
                request ?? new StoreOrderImportPriceVarianceDomesticPriceUpdateDto()
            );
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpdateImportPriceVarianceDomesticPrice failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 更新首次货柜价差异统计页展示的仓库当前进货价格。
    /// </summary>
    [HttpPost("import-price-variance/warehouse-import-price")]
    public async Task<IActionResult> UpdateImportPriceVarianceWarehouseImportPrice(
        [FromBody] StoreOrderImportPriceVarianceWarehouseImportPriceUpdateDto request
    )
    {
        try
        {
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await importPriceVarianceSlice.UpdateImportPriceVarianceWarehouseImportPriceAsync(
                request ?? new StoreOrderImportPriceVarianceWarehouseImportPriceUpdateDto()
            );
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpdateImportPriceVarianceWarehouseImportPrice failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 批量更新首次货柜价差异统计页展示的仓库当前进货价格。
    /// </summary>
    [HttpPost("import-price-variance/warehouse-import-price/batch")]
    public async Task<IActionResult> UpdateImportPriceVarianceWarehouseImportPriceBatch(
        [FromBody] StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateDto request
    )
    {
        try
        {
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await importPriceVarianceSlice.UpdateImportPriceVarianceWarehouseImportPriceBatchAsync(
                request
                    ?? new StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateDto()
            );
            if (result.Success)
            {
                return Ok(new { success = true, data = result.Data });
            }

            return BadRequest(new { success = false, message = result.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpdateImportPriceVarianceWarehouseImportPriceBatch failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }
}
