using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 国内供应商管理控制器
    /// 🏪 提供国内供应商数据的CRUD操作
    /// 🔐 包含完整的授权控制，确保只有有权限的用户才能访问相应功能
    /// </summary>
    [ApiController]
    [Route("api/v1/[controller]")]
    [Authorize] // 🔐 启用全局授权，所有端点都需要认证
    public class ChinaSuppliersController : ControllerBase
    {
        private readonly IChinaSupplierService _chinaSupplierService;
        private readonly ILogger<ChinaSuppliersController> _logger;

        public ChinaSuppliersController(
            IChinaSupplierService chinaSupplierService,
            ILogger<ChinaSuppliersController> logger
        )
        {
            _chinaSupplierService = chinaSupplierService;
            _logger = logger;
        }

        /// <summary>
        /// 获取国内供应商列表
        /// 📋 支持分页、搜索、筛选的国内供应商数据查询
        /// </summary>
        /// <param name="query">查询参数（分页、搜索条件等）</param>
        /// <returns>分页的国内供应商数据</returns>
        [HttpGet]
        public async Task<IActionResult> GetChinaSuppliers([FromQuery] ChinaSupplierQueryDto query)
        {
            try
            {
                var result = await _chinaSupplierService.GetChinaSuppliersAsync(query);
                if (result.Success)
                {
                    return Ok(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取国内供应商列表失败");
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<ChinaSupplierDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 根据GUID获取国内供应商详情
        /// </summary>
        [HttpGet("{guid}")]
        public async Task<IActionResult> GetChinaSupplierByGuid(string guid)
        {
            try
            {
                var result = await _chinaSupplierService.GetChinaSupplierByGuidAsync(guid);
                if (result.Success)
                {
                    return Ok(result);
                }
                if (result.ErrorCode == "CHINA_SUPPLIER_NOT_FOUND")
                {
                    return NotFound(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取国内供应商详情失败，GUID: {SupplierGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<ChinaSupplierDetailDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 创建新的国内供应商
        /// ➕ 只有Admin角色才能创建国内供应商，确保数据安全
        /// </summary>
        /// <param name="dto">创建国内供应商的数据传输对象</param>
        /// <returns>创建结果</returns>
        [HttpPost]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> CreateChinaSupplier([FromBody] CreateChinaSupplierDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<ChinaSupplierDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _chinaSupplierService.CreateChinaSupplierAsync(dto);
                if (result.Success)
                {
                    return CreatedAtAction(
                        nameof(GetChinaSupplierByGuid),
                        new { guid = result.Data?.Guid },
                        result
                    );
                }
                if (result.ErrorCode == "SUPPLIER_CODE_EXISTS")
                {
                    return Conflict(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建国内供应商失败");
                return StatusCode(
                    500,
                    ApiResponse<ChinaSupplierDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 更新国内供应商
        /// ✏️ 只有Admin角色才能更新国内供应商，确保数据完整性
        /// </summary>
        /// <param name="guid">国内供应商GUID</param>
        /// <param name="dto">更新国内供应商的数据传输对象</param>
        /// <returns>更新结果</returns>
        [HttpPut("{guid}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> UpdateChinaSupplier(
            string guid,
            [FromBody] UpdateChinaSupplierDto dto
        )
        {
            try
            {
                // 没有「查看国内供应商名称」权限的账号拿到的名称、联系方式都是空的，
                // 表单原样提交会把空值写回数据库，所以整单拒绝修改。
                if (!await HttpContext.CanViewDomesticSupplierNameAsync())
                {
                    return StatusCode(
                        StatusCodes.Status403Forbidden,
                        ApiResponse<ChinaSupplierDto>.Error(
                            "没有查看国内供应商名称的权限，不能修改供应商资料",
                            "DOMESTIC_SUPPLIER_NAME_FORBIDDEN"
                        )
                    );
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<ChinaSupplierDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _chinaSupplierService.UpdateChinaSupplierAsync(guid, dto);
                if (result.Success)
                {
                    return Ok(result);
                }
                if (result.ErrorCode == "CHINA_SUPPLIER_NOT_FOUND")
                {
                    return NotFound(result);
                }
                if (result.ErrorCode == "SUPPLIER_CODE_EXISTS")
                {
                    return Conflict(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新国内供应商失败，GUID: {SupplierGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<ChinaSupplierDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 删除国内供应商
        /// 🗑️ 只有Admin角色才能删除国内供应商，需要检查是否有相关业务数据
        /// </summary>
        /// <param name="guid">国内供应商GUID</param>
        /// <returns>删除结果</returns>
        [HttpDelete("{guid}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> DeleteChinaSupplier(string guid)
        {
            try
            {
                var result = await _chinaSupplierService.DeleteChinaSupplierAsync(guid);
                if (result.Success)
                {
                    return Ok(result);
                }
                if (result.ErrorCode == "CHINA_SUPPLIER_NOT_FOUND")
                {
                    return NotFound(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除国内供应商失败，GUID: {SupplierGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 启用/禁用国内供应商
        /// 🔄 Admin角色可以切换供应商状态
        /// </summary>
        /// <param name="guid">国内供应商GUID</param>
        /// <param name="status">状态值（0=禁用，1=启用）</param>
        /// <returns>更新结果</returns>
        [HttpPatch("{guid}/status/{status}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> ToggleSupplierStatus(string guid, int status)
        {
            try
            {
                if (status != 0 && status != 1)
                {
                    return BadRequest(
                        ApiResponse<ChinaSupplierDto>.Error(
                            "状态值必须为0（禁用）或1（启用）",
                            "INVALID_STATUS"
                        )
                    );
                }

                var result = await _chinaSupplierService.ToggleSupplierStatusAsync(guid, status);
                if (result.Success)
                {
                    return Ok(result);
                }
                if (result.ErrorCode == "CHINA_SUPPLIER_NOT_FOUND")
                {
                    return NotFound(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "切换国内供应商状态失败，GUID: {SupplierGUID}, Status: {Status}",
                    guid,
                    status
                );
                return StatusCode(
                    500,
                    ApiResponse<ChinaSupplierDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 检查供应商代码是否存在
        /// 🔍 用于表单验证，检查供应商代码的唯一性
        /// </summary>
        /// <param name="supplierCode">供应商代码</param>
        /// <param name="excludeGuid">排除的供应商GUID（用于编辑时排除自身）</param>
        /// <returns>检查结果</returns>
        [HttpGet("check-code/{supplierCode}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> CheckSupplierCodeExists(
            string supplierCode,
            [FromQuery] string? excludeGuid = null
        )
        {
            try
            {
                var result = await _chinaSupplierService.CheckSupplierCodeExistsAsync(
                    supplierCode,
                    excludeGuid
                );
                if (result.Success)
                {
                    return Ok(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "检查供应商代码是否存在失败，SupplierCode: {SupplierCode}",
                    supplierCode
                );
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取所有启用的国内供应商（下拉选择用）
        /// 📜 用于订单创建等场景的下拉选择列表
        /// </summary>
        /// <returns>启用的国内供应商列表</returns>
        [HttpGet("active")]
        public async Task<IActionResult> GetActiveChinaSuppliers()
        {
            try
            {
                var result = await _chinaSupplierService.GetActiveChinaSuppliersAsync();
                if (result.Success)
                {
                    return Ok(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取启用的国内供应商列表失败");
                return StatusCode(
                    500,
                    ApiResponse<List<ChinaSupplierDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 自动生成下一个供应商编码
        /// 🔢 根据现有编码生成下一个可用的供应商编码（HB+三位数字）
        /// </summary>
        /// <returns>生成的供应商编码</returns>
        [HttpGet("generate-code")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> GenerateNextSupplierCode()
        {
            try
            {
                var result = await _chinaSupplierService.GenerateNextSupplierCodeAsync();
                if (result.Success)
                {
                    return Ok(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成供应商编码失败");
                return StatusCode(
                    500,
                    ApiResponse<string>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取所有国内供应商（不分页）
        /// </summary>
        /// <returns>所有供应商列表</returns>
        [HttpGet("all")]
        public async Task<IActionResult> GetAllChinaSuppliers()
        {
            try
            {
                var result = await _chinaSupplierService.GetAllChinaSuppliersAsync();
                if (result.Success)
                {
                    return Ok(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取所有供应商失败");
                return StatusCode(
                    500,
                    ApiResponse<List<ChinaSupplierDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 同步选中的供应商到HBSales数据库
        /// </summary>
        [HttpPost("sync-to-hbsales")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> SyncToHbSales([FromBody] List<string> guids)
        {
            try
            {
                if (guids == null || guids.Count == 0)
                {
                    return BadRequest(
                        ApiResponse<SyncToHbSalesResultDto>.Error(
                            "请选择要同步的供应商",
                            "NO_SUPPLIERS_SELECTED"
                        )
                    );
                }

                var result = await _chinaSupplierService.SyncToHbSalesAsync(guids);
                if (result.Success)
                {
                    return Ok(result);
                }
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步供应商到HBSales失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncToHbSalesResultDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }
    }
}
