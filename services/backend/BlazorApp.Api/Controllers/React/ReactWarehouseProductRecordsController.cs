using BlazorApp.Api.Filters;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 只读仓库商品档案：摘要、货柜进货记录、分店配货统计。
    /// 摘要沿用 Warehouse.ManageProducts，货柜与配货沿用 Container.View，不新增权限。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/warehouse-product-records")]
    public class ReactWarehouseProductRecordsController : ControllerBase
    {
        private readonly IWarehouseProductRecordQueryService _service;
        private readonly ILogger<ReactWarehouseProductRecordsController> _logger;

        public ReactWarehouseProductRecordsController(
            IWarehouseProductRecordQueryService service,
            ILogger<ReactWarehouseProductRecordsController> logger
        )
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet("{productCode}/summary")]
        [Authorize(Policy = Permissions.Warehouse.ManageProducts)]
        public async Task<IActionResult> GetSummary(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return BadRequest(ApiResponse<WarehouseProductRecordSummaryDto>.Error("商品编码不能为空。", "BAD_REQUEST"));

            try
            {
                var data = await _service.GetSummaryAsync(productCode);
                if (data == null)
                    return NotFound(ApiResponse<WarehouseProductRecordSummaryDto>.Error("商品不存在。", "NOT_FOUND"));

                return Ok(ApiResponse<WarehouseProductRecordSummaryDto>.OK(data, "查询成功"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询仓库商品摘要失败: {ProductCode}", productCode);
                return StatusCode(500, ApiResponse<WarehouseProductRecordSummaryDto>.Error("服务器内部错误。"));
            }
        }

        [HttpPost("{productCode}/containers/query")]
        [Authorize(Policy = Permissions.Container.View)]
        [MaskContainerNumbers]
        public async Task<IActionResult> QueryContainers(
            string productCode,
            [FromBody] WarehouseProductRecordContainerQueryRequest request
        )
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return BadRequest(ApiResponse<WarehouseProductRecordContainerQueryResultDto>.Error("商品编码不能为空。", "BAD_REQUEST"));
            if (request == null)
                return BadRequest(ApiResponse<WarehouseProductRecordContainerQueryResultDto>.Error("请求参数不能为空。", "BAD_REQUEST"));

            try
            {
                var data = await _service.QueryContainersAsync(productCode, request);
                return Ok(ApiResponse<WarehouseProductRecordContainerQueryResultDto>.OK(data, "查询成功"));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<WarehouseProductRecordContainerQueryResultDto>.Error(ex.Message, "BAD_REQUEST"));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<WarehouseProductRecordContainerQueryResultDto>.Error(ex.Message, "NOT_FOUND"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询仓库商品货柜记录失败: {ProductCode}", productCode);
                return StatusCode(500, ApiResponse<WarehouseProductRecordContainerQueryResultDto>.Error("服务器内部错误。"));
            }
        }

        [HttpPost("{productCode}/allocations/query")]
        [Authorize(Policy = Permissions.Container.View)]
        public async Task<IActionResult> QueryAllocations(
            string productCode,
            [FromBody] WarehouseProductRecordAllocationQueryRequest request
        )
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return BadRequest(ApiResponse<WarehouseProductRecordAllocationQueryResultDto>.Error("商品编码不能为空。", "BAD_REQUEST"));
            if (request == null)
                return BadRequest(ApiResponse<WarehouseProductRecordAllocationQueryResultDto>.Error("请求参数不能为空。", "BAD_REQUEST"));

            try
            {
                var data = await _service.QueryAllocationsAsync(productCode, request);
                return Ok(ApiResponse<WarehouseProductRecordAllocationQueryResultDto>.OK(data, "查询成功"));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<WarehouseProductRecordAllocationQueryResultDto>.Error(ex.Message, "BAD_REQUEST"));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<WarehouseProductRecordAllocationQueryResultDto>.Error(ex.Message, "NOT_FOUND"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询仓库商品配货统计失败: {ProductCode}", productCode);
                return StatusCode(500, ApiResponse<WarehouseProductRecordAllocationQueryResultDto>.Error("服务器内部错误。"));
            }
        }
    }
}
