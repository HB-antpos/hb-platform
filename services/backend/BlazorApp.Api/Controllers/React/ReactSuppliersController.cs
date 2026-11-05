using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// React 供应商管理控制器
    /// 专门为React前端提供的API接口
    /// </summary>
    [ApiController]
    [Route("api/react/v1/suppliers")]
    [Authorize]
    [Obsolete(
        "Candidate for removal after confirming LocalSuppliersController fully replaces this endpoint."
    )]
    public class ReactSuppliersController : ControllerBase
    {
        private readonly IDomesticSupplierReactService _supplierReactService;
        private readonly ILogger<ReactSuppliersController> _logger;

        public ReactSuppliersController(
            IDomesticSupplierReactService supplierReactService,
            ILogger<ReactSuppliersController> logger
        )
        {
            _supplierReactService = supplierReactService;
            _logger = logger;
        }

        /// <summary>
        /// 获取所有启用的供应商列表（用于下拉选择）
        /// </summary>
        /// <returns>启用的供应商列表</returns>
        [HttpGet("list")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> GetActiveSupplierList()
        {
            try
            {
                var suppliers = await _supplierReactService.GetActiveSupplierListAsync();
                // 这里返回的是匿名对象，全局遮罩转换器挂不上，必须按权限手工处理：
                // 没有「查看国内供应商名称」权限时只返回供应商代码，名称、店铺号、联系人、电话都为空。
                var canViewName = await HttpContext.CanViewDomesticSupplierNameAsync();

                return Ok(
                    new
                    {
                        success = true,
                        data = suppliers.Select(s => new
                        {
                            code = s.SupplierCode,
                            name = DomesticSupplierInfoMasking.Apply(s.SupplierName, canViewName),
                            shopNumber = DomesticSupplierInfoMasking.Apply(s.ShopNumber, canViewName),
                            contactPerson = DomesticSupplierInfoMasking.Apply(s.ContactPerson, canViewName),
                            phone = DomesticSupplierInfoMasking.Apply(s.Phone, canViewName),
                        }),
                        message = "获取供应商列表成功",
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取启用供应商列表失败");
                return StatusCode(500, new { success = false, message = "获取供应商列表失败" });
            }
        }

        /// <summary>
        /// 根据供应商编码获取供应商详情
        /// </summary>
        /// <param name="supplierCode">供应商编码</param>
        /// <returns>供应商详情</returns>
        [HttpGet("{supplierCode}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> GetSupplierByCode(string supplierCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(supplierCode))
                {
                    return BadRequest(new { success = false, message = "供应商编码不能为空" });
                }

                var supplier = await _supplierReactService.GetSupplierByCodeAsync(supplierCode);

                if (supplier == null)
                {
                    return NotFound(new { success = false, message = "供应商不存在" });
                }

                return Ok(
                    new
                    {
                        success = true,
                        data = supplier,
                        message = "获取供应商详情成功",
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据编码获取供应商详情失败: {SupplierCode}", supplierCode);
                return StatusCode(500, new { success = false, message = "获取供应商详情失败" });
            }
        }
    }
}
