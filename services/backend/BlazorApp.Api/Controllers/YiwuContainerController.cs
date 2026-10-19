using BlazorApp.Api.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 义乌货柜管理控制器 - 基于新的Container模型
    /// </summary>
    [ApiController]
    [Route("api/v1/yiwu-containers")]
    [Authorize]
    // 旧接口同样返回货柜编号：无「查看完整货柜编号」权限的账号只拿到后 4 位。
    [MaskContainerNumbers]
    public class YiwuContainerController : ControllerBase
    {
        private readonly IYiwuContainerService _containerService;
        private readonly ILogger<YiwuContainerController> _logger;

        public YiwuContainerController(
            IYiwuContainerService containerService,
            ILogger<YiwuContainerController> logger)
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

        #region 货柜主表操作

        /// <summary>
        /// 获取货柜列表
        /// </summary>
        /// <param name="request">查询请求</param>
        /// <returns>货柜列表</returns>
        [HttpPost("list")]
        [Authorize]
        public async Task<IActionResult> GetContainers([FromBody] YiwuContainerQueryRequest request)
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
        /// 获取货柜详情
        /// </summary>
        /// <param name="containerCode">货柜编码</param>
        /// <returns>货柜详情</returns>
        [HttpGet("{containerCode}")]
        [Authorize]
        public async Task<IActionResult> GetContainer(string containerCode)
        {
            try
            {
                if (string.IsNullOrEmpty(containerCode))
                {
                    return BadRequest(new { success = false, message = "货柜编码不能为空" });
                }

                var result = await _containerService.GetContainerAsync(containerCode);
                if (result == null)
                {
                    return NotFound(new { success = false, message = "货柜不存在" });
                }

                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜详情失败, ContainerCode: {ContainerCode}", containerCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 创建货柜
        /// </summary>
        /// <param name="containerDto">货柜DTO</param>
        /// <returns>创建结果</returns>
        [HttpPost]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> CreateContainer([FromBody] YiwuContainerDto containerDto)
        {
            try
            {
                if (containerDto == null)
                {
                    return BadRequest(new { success = false, message = "货柜信息不能为空" });
                }

                var containerCode = await _containerService.CreateContainerAsync(containerDto);
                return Ok(new { success = true, data = new { containerCode }, message = "创建成功" });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "创建货柜");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建货柜失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 更新货柜
        /// </summary>
        /// <param name="containerCode">货柜编码</param>
        /// <param name="containerDto">货柜DTO</param>
        /// <returns>更新结果</returns>
        [HttpPut("{containerCode}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> UpdateContainer(string containerCode, [FromBody] YiwuContainerDto containerDto)
        {
            try
            {
                if (string.IsNullOrEmpty(containerCode))
                {
                    return BadRequest(new { success = false, message = "货柜编码不能为空" });
                }

                if (containerDto == null)
                {
                    return BadRequest(new { success = false, message = "货柜信息不能为空" });
                }

                if (containerCode != containerDto.ContainerCode)
                {
                    return BadRequest(new { success = false, message = "路径中的货柜编码与数据中的不一致" });
                }

                var success = await _containerService.UpdateContainerAsync(containerDto);
                if (!success)
                {
                    return NotFound(new { success = false, message = "货柜不存在或已删除" });
                }

                return Ok(new { success = true, message = "更新成功" });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "更新货柜");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新货柜失败, ContainerCode: {ContainerCode}", containerCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 删除货柜
        /// </summary>
        /// <param name="containerCode">货柜编码</param>
        /// <returns>删除结果</returns>
        [HttpDelete("{containerCode}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteContainer(string containerCode)
        {
            try
            {
                if (string.IsNullOrEmpty(containerCode))
                {
                    return BadRequest(new { success = false, message = "货柜编码不能为空" });
                }

                var success = await _containerService.DeleteContainerAsync(containerCode);
                if (!success)
                {
                    return NotFound(new { success = false, message = "货柜不存在或已删除" });
                }

                return Ok(new { success = true, message = "删除成功" });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "删除货柜");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除货柜失败, ContainerCode: {ContainerCode}", containerCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量删除货柜
        /// </summary>
        /// <param name="containerCodes">货柜编码列表</param>
        /// <returns>批量操作结果</returns>
        [HttpPost("batch-delete")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> BatchDeleteContainers([FromBody] List<string> containerCodes)
        {
            try
            {
                if (containerCodes == null || !containerCodes.Any())
                {
                    return BadRequest(new { success = false, message = "货柜编码列表不能为空" });
                }

                var result = await _containerService.BatchDeleteContainersAsync(containerCodes);
                return Ok(new { success = result.Success, data = result });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "批量删除货柜");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除货柜失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        #endregion

        #region 货柜明细操作

        /// <summary>
        /// 获取货柜明细列表
        /// </summary>
        /// <param name="containerCode">货柜编码</param>
        /// <returns>明细列表</returns>
        [HttpGet("{containerCode}/details")]
        [Authorize]
        public async Task<IActionResult> GetContainerDetails(string containerCode)
        {
            try
            {
                if (string.IsNullOrEmpty(containerCode))
                {
                    return BadRequest(new { success = false, message = "货柜编码不能为空" });
                }

                var result = await _containerService.GetContainerDetailsAsync(containerCode);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜明细列表失败, ContainerCode: {ContainerCode}", containerCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取货柜明细详情
        /// </summary>
        /// <param name="detailCode">明细编码</param>
        /// <returns>明细详情</returns>
        [HttpGet("details/{detailCode}")]
        [Authorize]
        public async Task<IActionResult> GetContainerDetail(string detailCode)
        {
            try
            {
                if (string.IsNullOrEmpty(detailCode))
                {
                    return BadRequest(new { success = false, message = "明细编码不能为空" });
                }

                var result = await _containerService.GetContainerDetailAsync(detailCode);
                if (result == null)
                {
                    return NotFound(new { success = false, message = "明细不存在" });
                }

                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜明细详情失败, DetailCode: {DetailCode}", detailCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 创建货柜明细
        /// </summary>
        /// <param name="detailDto">明细DTO</param>
        /// <returns>创建结果</returns>
        [HttpPost("details")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> CreateContainerDetail([FromBody] YiwuContainerDetailDto detailDto)
        {
            try
            {
                if (detailDto == null)
                {
                    return BadRequest(new { success = false, message = "明细信息不能为空" });
                }

                var detailCode = await _containerService.CreateContainerDetailAsync(detailDto);
                return Ok(new { success = true, data = new { detailCode }, message = "创建成功" });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "创建货柜明细");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建货柜明细失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 更新货柜明细
        /// </summary>
        /// <param name="detailCode">明细编码</param>
        /// <param name="detailDto">明细DTO</param>
        /// <returns>更新结果</returns>
        [HttpPut("details/{detailCode}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> UpdateContainerDetail(string detailCode, [FromBody] YiwuContainerDetailDto detailDto)
        {
            try
            {
                if (string.IsNullOrEmpty(detailCode))
                {
                    return BadRequest(new { success = false, message = "明细编码不能为空" });
                }

                if (detailDto == null)
                {
                    return BadRequest(new { success = false, message = "明细信息不能为空" });
                }

                if (detailCode != detailDto.DetailCode)
                {
                    return BadRequest(new { success = false, message = "路径中的明细编码与数据中的不一致" });
                }

                var success = await _containerService.UpdateContainerDetailAsync(detailDto);
                if (!success)
                {
                    return NotFound(new { success = false, message = "明细不存在或已删除" });
                }

                return Ok(new { success = true, message = "更新成功" });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "更新货柜明细");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新货柜明细失败, DetailCode: {DetailCode}", detailCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 删除货柜明细
        /// </summary>
        /// <param name="detailCode">明细编码</param>
        /// <returns>删除结果</returns>
        [HttpDelete("details/{detailCode}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> DeleteContainerDetail(string detailCode)
        {
            try
            {
                if (string.IsNullOrEmpty(detailCode))
                {
                    return BadRequest(new { success = false, message = "明细编码不能为空" });
                }

                var success = await _containerService.DeleteContainerDetailAsync(detailCode);
                if (!success)
                {
                    return NotFound(new { success = false, message = "明细不存在或已删除" });
                }

                return Ok(new { success = true, message = "删除成功" });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "删除货柜明细");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除货柜明细失败, DetailCode: {DetailCode}", detailCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量添加货柜明细（通过货号和件数）
        /// </summary>
        /// <param name="request">批量添加请求</param>
        /// <returns>批量操作结果</returns>
        [HttpPost("details/batch-add")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> BatchAddContainerDetails([FromBody] BatchAddYiwuContainerDetailsRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrEmpty(request.ContainerCode) || !request.Details.Any())
                {
                    return BadRequest(new { success = false, message = "请求数据不完整" });
                }

                var result = await _containerService.BatchAddContainerDetailsAsync(request);
                return Ok(new { success = result.Success, data = result });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "批量添加货柜明细");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量添加货柜明细失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量删除货柜明细
        /// </summary>
        /// <param name="detailCodes">明细编码列表</param>
        /// <returns>批量操作结果</returns>
        [HttpPost("details/batch-delete")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> BatchDeleteContainerDetails([FromBody] List<string> detailCodes)
        {
            try
            {
                if (detailCodes == null || !detailCodes.Any())
                {
                    return BadRequest(new { success = false, message = "明细编码列表不能为空" });
                }

                var result = await _containerService.BatchDeleteContainerDetailsAsync(detailCodes);
                return Ok(new { success = result.Success, data = result });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "批量删除货柜明细");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除货柜明细失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量更新货柜明细
        /// </summary>
        /// <param name="details">明细列表</param>
        /// <returns>批量操作结果</returns>
        [HttpPost("details/batch-update")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> BatchUpdateContainerDetails([FromBody] List<YiwuContainerDetailDto> details)
        {
            try
            {
                if (details == null || !details.Any())
                {
                    return BadRequest(new { success = false, message = "明细列表不能为空" });
                }

                var result = await _containerService.BatchUpdateContainerDetailsAsync(details);
                return Ok(new { success = result.Success, data = result });
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

        #endregion

        #region 业务逻辑

        /// <summary>
        /// 重新计算货柜汇总信息
        /// </summary>
        /// <param name="containerCode">货柜编码</param>
        /// <returns>计算结果</returns>
        [HttpPost("{containerCode}/recalculate")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> RecalculateContainerSummary(string containerCode)
        {
            try
            {
                if (string.IsNullOrEmpty(containerCode))
                {
                    return BadRequest(new { success = false, message = "货柜编码不能为空" });
                }

                var success = await _containerService.RecalculateContainerSummaryAsync(containerCode);
                if (!success)
                {
                    return NotFound(new { success = false, message = "货柜不存在" });
                }

                return Ok(new { success = true, message = "重新计算成功" });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "重新计算货柜汇总");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新计算货柜汇总信息失败, ContainerCode: {ContainerCode}", containerCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 分摊运输成本到各个商品
        /// </summary>
        /// <param name="containerCode">货柜编码</param>
        /// <returns>分摊结果</returns>
        [HttpPost("{containerCode}/allocate-transport-cost")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> AllocateTransportCost(string containerCode)
        {
            try
            {
                if (string.IsNullOrEmpty(containerCode))
                {
                    return BadRequest(new { success = false, message = "货柜编码不能为空" });
                }

                var success = await _containerService.AllocateTransportCostAsync(containerCode);
                if (!success)
                {
                    return NotFound(new { success = false, message = "货柜不存在或无运费需要分摊" });
                }

                return Ok(new { success = true, message = "运输成本分摊成功" });
            }
            catch (Exception ex) when (ContainerMutationLock.TryResolveConflict(ex, out _))
            {
                return CreateContainerMutationConflictResponse(ex, "分摊货柜运输成本");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分摊运输成本失败, ContainerCode: {ContainerCode}", containerCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 根据商品信息查询相关货柜
        /// </summary>
        /// <param name="itemNumber">商品货号</param>
        /// <returns>相关货柜列表</returns>
        [HttpGet("by-item/{itemNumber}")]
        [Authorize]
        public async Task<IActionResult> GetContainersByItemNumber(string itemNumber)
        {
            try
            {
                if (string.IsNullOrEmpty(itemNumber))
                {
                    return BadRequest(new { success = false, message = "商品货号不能为空" });
                }

                var result = await _containerService.GetContainersByItemNumberAsync(itemNumber);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据商品信息查询相关货柜失败, ItemNumber: {ItemNumber}", itemNumber);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取货柜状态选项
        /// </summary>
        /// <returns>状态选项列表</returns>
        [HttpGet("status-options")]
        [Authorize]
        public async Task<IActionResult> GetContainerStatusOptions()
        {
            try
            {
                var result = await _containerService.GetContainerStatusOptionsAsync();
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜状态选项失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 验证货柜明细数据
        /// </summary>
        /// <param name="details">明细列表</param>
        /// <returns>验证结果</returns>
        [HttpPost("validate-details")]
        [Authorize]
        public async Task<IActionResult> ValidateContainerDetails([FromBody] List<BatchYiwuContainerDetailItem> details)
        {
            try
            {
                if (details == null)
                {
                    return BadRequest(new { success = false, message = "明细列表不能为空" });
                }

                var result = await _containerService.ValidateContainerDetailsAsync(details);
                return Ok(new { success = result.IsValid, data = new { isValid = result.IsValid, errors = result.Errors } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证货柜明细数据失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量翻译商品名称
        /// </summary>
        /// <param name="chineseNames">中文名称列表</param>
        /// <returns>翻译结果</returns>
        [HttpPost("batch-translate")]
        [Authorize]
        public async Task<IActionResult> BatchTranslateProductNames([FromBody] List<string> chineseNames)
        {
            try
            {
                if (chineseNames == null || !chineseNames.Any())
                {
                    return BadRequest(new { success = false, message = "请提供需要翻译的中文名称" });
                }

                var result = await _containerService.BatchTranslateProductNamesAsync(chineseNames);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量翻译商品名称失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量更新国内商品信息
        /// </summary>
        /// <param name="products">商品信息列表</param>
        /// <returns>更新结果</returns>
        [HttpPost("batch-update-products")]
        [Authorize]
        public async Task<IActionResult> BatchUpdateDomesticProducts([FromBody] List<DomesticProductDto> products)
        {
            try
            {
                if (products == null || !products.Any())
                {
                    return BadRequest(new { success = false, message = "请提供需要更新的商品信息" });
                }

                var result = await _containerService.BatchUpdateDomesticProductsAsync(products);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新国内商品信息失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        #region 导出功能

        /// <summary>
        /// 导出货柜明细到Excel
        /// </summary>
        /// <param name="containerCode">货柜编码</param>
        /// <param name="request">导出请求</param>
        /// <returns>Excel文件</returns>
        [HttpPost("{containerCode}/export/excel")]
        [Authorize(Roles = "Admin,Manager,Viewer")]
        public async Task<IActionResult> ExportToExcel(string containerCode, [FromBody] ContainerDetailsExportRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(containerCode))
                {
                    return BadRequest(new { success = false, message = "货柜编码不能为空" });
                }

                if (request == null)
                {
                    return BadRequest(new { success = false, message = "导出请求不能为空" });
                }

                // 是否写完整货柜编号只看账号权限，不信任客户端。
                request.ShowFullContainerNumber = await HttpContext.CanViewFullContainerNumberAsync();
                var result = await _containerService.ExportContainerDetailsToExcelAsync(request);

                if (result.Success && result.Data != null)
                {
                    return Ok(new { success = true, data = result.Data });
                }
                else
                {
                    return BadRequest(new { success = false, message = result.Message });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出Excel失败: ContainerCode={ContainerCode}", containerCode);
                return StatusCode(500, new { success = false, message = "导出失败，请稍后重试" });
            }
        }

        /// <summary>
        /// 导出货柜明细到PDF
        /// </summary>
        /// <param name="containerCode">货柜编码</param>
        /// <param name="request">导出请求</param>
        /// <returns>PDF文件</returns>
        [HttpPost("{containerCode}/export/pdf")]
        [Authorize(Roles = "Admin,Manager,Viewer")]
        public async Task<IActionResult> ExportToPdf(string containerCode, [FromBody] ContainerDetailsExportRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(containerCode))
                {
                    return BadRequest(new { success = false, message = "货柜编码不能为空" });
                }

                if (request == null)
                {
                    return BadRequest(new { success = false, message = "导出请求不能为空" });
                }

                // 是否写完整货柜编号只看账号权限，不信任客户端。
                request.ShowFullContainerNumber = await HttpContext.CanViewFullContainerNumberAsync();
                var result = await _containerService.ExportContainerDetailsToPdfAsync(request);

                if (result.Success && result.Data != null)
                {
                    return Ok(new { success = true, data = result.Data });
                }
                else
                {
                    return BadRequest(new { success = false, message = result.Message });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出PDF失败: ContainerCode={ContainerCode}", containerCode);
                return StatusCode(500, new { success = false, message = "导出失败，请稍后重试" });
            }
        }

        #endregion
    }

}
#endregion
