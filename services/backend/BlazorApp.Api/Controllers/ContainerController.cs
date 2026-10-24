using BlazorApp.Api.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 货柜管理控制器
    /// </summary>
    [ApiController]
    [Route("api/v1/[controller]")]
    [Authorize]
    // 旧接口同样返回货柜编号：无「查看完整货柜编号」权限的账号只拿到后 4 位。
    [MaskContainerNumbers]
    public class ContainerController : ControllerBase
    {
        private readonly IContainerService _containerService;
        private readonly ILogger<ContainerController> _logger;

        public ContainerController(
            IContainerService containerService,
            ILogger<ContainerController> logger)
        {
            _containerService = containerService;
            _logger = logger;
        }

        private IActionResult CreateContainerMutationConflictResponse(
            Exception exception,
            string operation
        )
        {
            ContainerMutationLock.TryResolveConflict(exception, out var conflict);
            Response.Headers.RetryAfter = "1";
            _logger.LogWarning(
                exception,
                "{Operation}遇到货柜并发冲突, ResultCode: {ResultCode}",
                operation,
                conflict?.ResultCode
            );
            return StatusCode(
                StatusCodes.Status409Conflict,
                new
                {
                    success = false,
                    code = ContainerMutationLock.BusyErrorCode,
                    message = "同一货柜正在保存，请稍后重试",
                }
            );
        }

        /// <summary>
        /// 获取货柜列表
        /// </summary>
        /// <param name="request">查询请求</param>
        /// <returns>货柜列表</returns>
        [HttpPost("list")]
        [Authorize]
        public async Task<IActionResult> GetContainers([FromBody] ContainerQueryRequest request)
        {
            try
            {
                var result = await _containerService.GetContainersAsync(request);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜列表失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 更新货柜信息
        /// </summary>
        /// <param name="containerGuid">货柜GUID</param>
        /// <param name="dto">更新DTO</param>
        /// <returns>更新结果</returns>
        [HttpPut("{containerGuid}")]
        [Authorize]
        public async Task<IActionResult> UpdateContainer(string containerGuid, [FromBody] UpdateContainerDto dto)
        {
            try
            {
                if (string.IsNullOrEmpty(containerGuid))
                {
                    return BadRequest(new { success = false, message = "货柜GUID不能为空" });
                }

                if (dto == null)
                {
                    return BadRequest(new { success = false, message = "更新数据不能为空" });
                }

                // 与 React 接口一致：遮罩值或无「查看完整货柜编号」权限时不修改货柜编号。
                if (
                    dto.货柜编号 != null
                    && (
                        ContainerNumberMasking.IsMasked(dto.货柜编号)
                        || !await HttpContext.CanViewFullContainerNumberAsync()
                    )
                )
                {
                    dto.货柜编号 = null;
                }

                var result = await _containerService.UpdateContainerAsync(containerGuid, dto);

                if (result)
                {
                    return Ok(new { success = true, message = "更新成功" });
                }
                else
                {
                    return NotFound(new { success = false, message = "货柜不存在或更新失败" });
                }
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "更新货柜信息");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新货柜信息失败, ContainerGuid: {ContainerGuid}", containerGuid);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取货柜详情
        /// </summary>
        /// <param name="containerGuid">货柜GUID</param>
        /// <returns>货柜详情</returns>
        [HttpGet("{containerGuid}")]
        [Authorize]
        public async Task<IActionResult> GetContainerDetail(string containerGuid)
        {
            try
            {
                if (string.IsNullOrEmpty(containerGuid))
                {
                    return BadRequest(new { success = false, message = "货柜GUID不能为空" });
                }

                var result = await _containerService.GetContainerDetailAsync(containerGuid);
                if (result == null)
                {
                    return NotFound(new { success = false, message = "货柜不存在" });
                }

                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜详情失败, ContainerGuid: {ContainerGuid}", containerGuid);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取货柜商品列表
        /// </summary>
        /// <param name="containerGuid">货柜GUID</param>
        /// <returns>商品列表</returns>
        [HttpGet("{containerGuid}/products")]
        [Authorize]
        public async Task<IActionResult> GetContainerProducts(string containerGuid)
        {
            try
            {
                if (string.IsNullOrEmpty(containerGuid))
                {
                    return BadRequest(new { success = false, message = "货柜GUID不能为空" });
                }

                var result = await _containerService.GetContainerProductsAsync(containerGuid);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜商品列表失败, ContainerGuid: {ContainerGuid}", containerGuid);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取符合条件的所有货柜商品明细列表
        /// </summary>
        /// <param name="request">查询请求</param>
        /// <returns>商品明细列表</returns>
        [HttpPost("filtered-products")]
        [Authorize]
        public async Task<IActionResult> GetFilteredContainerProducts([FromBody] ContainerQueryRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { success = false, message = "请求参数不能为空" });
                }

                var result = await _containerService.GetFilteredContainerProductsAsync(request);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜商品明细列表失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取日期过滤选项
        /// </summary>
        /// <returns>日期选项列表</returns>
        [HttpGet("date-filter-options")]
        [Authorize]
        public async Task<IActionResult> GetDateFilterOptions()
        {
            try
            {
                var result = await _containerService.GetDateFilterOptionsAsync();
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取日期过滤选项失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量更新货柜明细
        /// </summary>
        /// <param name="updates">明细更新列表</param>
        /// <returns>更新结果</returns>
        [HttpPost("batch-update-details")]
        [Authorize]
        public async Task<IActionResult> BatchUpdateDetails([FromBody] List<UpdateContainerDetailDto> updates)
        {
            try
            {
                if (updates == null || !updates.Any())
                {
                    return BadRequest(new { success = false, message = "更新列表不能为空" });
                }

                var totalUpdated = await _containerService.BatchUpdateDetailsAsync(updates);

                return Ok(new
                {
                    success = true,
                    message = $"成功更新 {totalUpdated} 条明细",
                    data = new { totalUpdated, totalRequested = updates.Count }
                });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "批量更新货柜明细");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新货柜明细失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 创建新货柜
        /// </summary>
        /// <param name="dto">创建货柜DTO</param>
        /// <returns>创建结果</returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateContainer([FromBody] CreateContainerDto dto)
        {
            try
            {
                if (dto == null)
                {
                    return BadRequest(new { success = false, message = "创建数据不能为空" });
                }

                if (string.IsNullOrWhiteSpace(dto.货柜编号))
                {
                    return BadRequest(new { success = false, message = "货柜编号不能为空" });
                }

                var containerGuid = await _containerService.CreateContainerAsync(dto);

                return Ok(new
                {
                    success = true,
                    message = "创建成功",
                    data = new { containerGuid }
                });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "创建货柜");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "创建货柜失败: {Message}", ex.Message);
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建货柜失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }
    }
}
