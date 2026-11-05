using System.Security.Claims;
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
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DomesticSupplierController : ControllerBase
    {
        private readonly IDomesticSupplierService _supplierService;
        private readonly ILogger<DomesticSupplierController> _logger;

        public DomesticSupplierController(
            IDomesticSupplierService supplierService,
            ILogger<DomesticSupplierController> logger
        )
        {
            _supplierService = supplierService;
            _logger = logger;
        }

        /// <summary>
        /// 获取分页供应商列表
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>分页结果</returns>
        [HttpGet]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> GetSuppliers([FromQuery] DomesticSupplierQueryDto query)
        {
            try
            {
                var result = await _supplierService.GetSuppliersAsync(query);
                return Ok(
                    ApiResponse<PagedResult<DomesticSupplierDto>>.OK(result, "获取供应商列表成功")
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取供应商列表失败");
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("获取供应商列表失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID获取供应商详情
        /// </summary>
        /// <param name="guid">供应商GUID</param>
        /// <returns>供应商详情</returns>
        [HttpGet("{guid}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> GetSupplierByGuid(string guid)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(guid))
                {
                    return BadRequest(
                        ApiResponse<object>.Error("供应商GUID不能为空", "INVALID_PARAMETER")
                    );
                }

                var supplier = await _supplierService.GetSupplierByGuidAsync(guid);
                if (supplier == null)
                {
                    return NotFound(
                        ApiResponse<object>.Error("供应商不存在", "SUPPLIER_NOT_FOUND")
                    );
                }

                return Ok(ApiResponse<DomesticSupplierDto>.OK(supplier, "获取供应商详情成功"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取供应商详情失败: {Guid}", guid);
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("获取供应商详情失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据供应商编码获取供应商详情
        /// </summary>
        /// <param name="supplierCode">供应商编码</param>
        /// <returns>供应商详情</returns>
        [HttpGet("by-code/{supplierCode}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> GetSupplierByCode(string supplierCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(supplierCode))
                {
                    return BadRequest(
                        ApiResponse<object>.Error("供应商编码不能为空", "INVALID_PARAMETER")
                    );
                }

                var supplier = await _supplierService.GetSupplierByCodeAsync(supplierCode);
                if (supplier == null)
                {
                    return NotFound(
                        ApiResponse<object>.Error("供应商不存在", "SUPPLIER_NOT_FOUND")
                    );
                }

                return Ok(ApiResponse<DomesticSupplierDto>.OK(supplier, "获取供应商详情成功"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据编码获取供应商详情失败: {SupplierCode}", supplierCode);
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("获取供应商详情失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 创建新供应商
        /// </summary>
        /// <param name="dto">创建供应商请求DTO</param>
        /// <returns>创建的供应商信息</returns>
        [HttpPost]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> CreateSupplier([FromBody] CreateDomesticSupplierDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = ModelState.SelectMany(x =>
                        x.Value?.Errors?.Select(e => e.ErrorMessage)
                        ?? System.Linq.Enumerable.Empty<string>()
                    );
                    return BadRequest(
                        ApiResponse<object>.Error(
                            $"输入验证失败: {string.Join(", ", errors)}",
                            "VALIDATION_ERROR"
                        )
                    );
                }

                var currentUser = User.Identity?.Name ?? "系统";
                var supplier = await _supplierService.CreateSupplierAsync(dto, currentUser);

                return CreatedAtAction(
                    nameof(GetSupplierByGuid),
                    new { guid = supplier.Guid },
                    ApiResponse<DomesticSupplierDto>.OK(supplier, "创建供应商成功")
                );
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "创建供应商业务逻辑错误: {SupplierCode}", dto.SupplierCode);
                return BadRequest(ApiResponse<object>.Error(ex.Message, "BUSINESS_ERROR"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建供应商失败: {SupplierCode}", dto.SupplierCode);
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("创建供应商失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 更新供应商信息
        /// </summary>
        /// <param name="guid">供应商GUID</param>
        /// <param name="dto">更新供应商请求DTO</param>
        /// <returns>更新后的供应商信息</returns>
        [HttpPut("{guid}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> UpdateSupplier(
            string guid,
            [FromBody] UpdateDomesticSupplierDto dto
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
                        ApiResponse<object>.Error(
                            "没有查看国内供应商名称的权限，不能修改供应商资料",
                            "DOMESTIC_SUPPLIER_NAME_FORBIDDEN"
                        )
                    );
                }

                if (string.IsNullOrWhiteSpace(guid))
                {
                    return BadRequest(
                        ApiResponse<object>.Error("供应商GUID不能为空", "INVALID_PARAMETER")
                    );
                }

                if (!ModelState.IsValid)
                {
                    var errors = ModelState.SelectMany(x =>
                        x.Value?.Errors?.Select(e => e.ErrorMessage)
                        ?? System.Linq.Enumerable.Empty<string>()
                    );
                    return BadRequest(
                        ApiResponse<object>.Error(
                            $"输入验证失败: {string.Join(", ", errors)}",
                            "VALIDATION_ERROR"
                        )
                    );
                }

                var currentUser = User.Identity?.Name ?? "系统";
                var supplier = await _supplierService.UpdateSupplierAsync(guid, dto, currentUser);

                if (supplier == null)
                {
                    return NotFound(
                        ApiResponse<object>.Error("供应商不存在", "SUPPLIER_NOT_FOUND")
                    );
                }

                return Ok(ApiResponse<DomesticSupplierDto>.OK(supplier, "更新供应商成功"));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "更新供应商业务逻辑错误: {Guid}", guid);
                return BadRequest(ApiResponse<object>.Error(ex.Message, "BUSINESS_ERROR"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新供应商失败: {Guid}", guid);
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("更新供应商失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 删除供应商
        /// </summary>
        /// <param name="guid">供应商GUID</param>
        /// <returns>操作结果</returns>
        [HttpDelete("{guid}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> DeleteSupplier(string guid)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(guid))
                {
                    return BadRequest(
                        ApiResponse<object>.Error("供应商GUID不能为空", "INVALID_PARAMETER")
                    );
                }

                var success = await _supplierService.DeleteSupplierAsync(guid);
                if (!success)
                {
                    return NotFound(
                        ApiResponse<object>.Error("供应商不存在", "SUPPLIER_NOT_FOUND")
                    );
                }

                return Ok(ApiResponse<object>.CreateSuccess("删除供应商成功"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除供应商失败: {Guid}", guid);
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("删除供应商失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 检查供应商编码是否已存在
        /// </summary>
        /// <param name="supplierCode">供应商编码</param>
        /// <param name="excludeGuid">排除的GUID（用于更新时检查）</param>
        /// <returns>是否已存在</returns>
        [HttpGet("check-code/{supplierCode}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> CheckSupplierCodeExists(
            string supplierCode,
            [FromQuery] string? excludeGuid = null
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(supplierCode))
                {
                    return BadRequest(
                        ApiResponse<object>.Error("供应商编码不能为空", "INVALID_PARAMETER")
                    );
                }

                var exists = await _supplierService.IsSupplierCodeExistsAsync(
                    supplierCode,
                    excludeGuid
                );
                return Ok(ApiResponse<bool>.OK(exists, "检查供应商编码完成"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查供应商编码失败: {SupplierCode}", supplierCode);
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("检查供应商编码失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 生成下一个可用的供应商编码（HB+3位序号）
        /// </summary>
        /// <returns>生成的供应商编码</returns>
        [HttpGet("generate-code")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> GenerateNextSupplierCode()
        {
            try
            {
                var code = await _supplierService.GenerateNextSupplierCodeAsync();
                return Ok(ApiResponse<string>.OK(code, "生成供应商编码成功"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成供应商编码失败");
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("生成供应商编码失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 启用/禁用供应商
        /// </summary>
        /// <param name="guid">供应商GUID</param>
        /// <param name="status">状态（1=启用，0=禁用）</param>
        /// <returns>操作结果</returns>
        [HttpPatch("{guid}/status")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> UpdateSupplierStatus(string guid, [FromBody] int status)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(guid))
                {
                    return BadRequest(
                        ApiResponse<object>.Error("供应商GUID不能为空", "INVALID_PARAMETER")
                    );
                }

                if (status != 0 && status != 1)
                {
                    return BadRequest(
                        ApiResponse<object>.Error("状态值无效，必须为0或1", "INVALID_STATUS")
                    );
                }

                var currentUser = User.Identity?.Name ?? "系统";
                var success = await _supplierService.UpdateSupplierStatusAsync(
                    guid,
                    status,
                    currentUser
                );

                if (!success)
                {
                    return NotFound(
                        ApiResponse<object>.Error("供应商不存在", "SUPPLIER_NOT_FOUND")
                    );
                }

                var statusText = status == 1 ? "启用" : "禁用";
                return Ok(ApiResponse<object>.CreateSuccess($"{statusText}供应商成功"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新供应商状态失败: {Guid}", guid);
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("更新供应商状态失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取所有启用的供应商列表（用于下拉选择）
        /// </summary>
        /// <returns>启用的供应商列表</returns>
        [HttpGet("active-list")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> GetActiveSupplierList()
        {
            try
            {
                var suppliers = await _supplierService.GetActiveSupplierListAsync();
                return Ok(
                    ApiResponse<List<DomesticSupplierDto>>.OK(suppliers, "获取启用供应商列表成功")
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取启用供应商列表失败");
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("获取启用供应商列表失败", "INTERNAL_SERVER_ERROR")
                );
            }
        }
    }
}
