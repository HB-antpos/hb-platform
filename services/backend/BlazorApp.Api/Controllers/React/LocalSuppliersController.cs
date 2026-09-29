using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/local-suppliers")]
    [Authorize]
    public class LocalSuppliersController : ControllerBase
    {
        private readonly ILocalSuppliersReactService _service;
        private readonly ILogger<LocalSuppliersController> _logger;

        public LocalSuppliersController(ILocalSuppliersReactService service, ILogger<LocalSuppliersController> logger)
        {
            _service = service;
            _logger = logger;
        }

        // 分页列表是 /pos-admin/suppliers 页面专用，按 AustralianSuppliers.View 授权；
        // active / check-code 是多个页面的下拉数据源，保持登录即可访问。
        [HttpGet]
        [Authorize(Policy = Permissions.AustralianSuppliers.View)]
        public async Task<IActionResult> GetList([FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20, [FromQuery] string? keyword = null, [FromQuery] int? status = null, [FromQuery] string? sortBy = null, [FromQuery] string? sortOrder = null)
        {
            try
            {
                var res = await _service.GetSuppliersAsync(pageIndex, pageSize, keyword, status, sortBy, sortOrder);
                return Ok(new { success = true, data = new { items = res.Items, total = res.Total }, message = "获取成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取本地供应商列表失败");
                return StatusCode(500, new { success = false, message = "获取失败" });
            }
        }

        [HttpGet("active")]

        public async Task<IActionResult> GetActive()
        {
            try
            {
                var res = await _service.GetActiveSuppliersAsync();
                return Ok(new { success = true, data = res, message = "获取成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取启用本地供应商失败");
                return StatusCode(500, new { success = false, message = "获取失败" });
            }
        }

        // 写操作从角色硬编码改为权限策略，WarehouseManager 通过角色模板保留原有能力。
        // 2026-09-29 起停用：HQ 供应商字典 → 本地 HBLocalSupplier 属于 HQ → HBweb 方向，统一返回 410；sync-to-hq 保持可用。
        [HttpPost("sync")]
        [Authorize(Policy = Permissions.AustralianSuppliers.Edit)]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> Sync([FromBody] SyncRequest? body)
        {
            try
            {
                var res = await _service.SyncFromDicAsync(body?.Since, body?.Overwrite ?? true);
                return Ok(new { success = res.Success, data = res.Data, message = res.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步本地供应商失败");
                return StatusCode(500, new { success = false, message = "同步失败" });
            }
        }

        public class SyncRequest
        {
            public DateTime? Since { get; set; }
            public bool Overwrite { get; set; } = true;
        }

        [HttpPost("sync-to-hq")]
        [Authorize(Policy = Permissions.AustralianSuppliers.Edit)]
        public async Task<IActionResult> SyncToHq([FromBody] SyncToHqRequest? body)
        {
            try
            {
                var res = await _service.SyncToHqAsync(
                    body?.SupplierCodes ?? new List<string>()
                );
                return Ok(new { success = res.Success, data = res.Data, message = res.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步澳洲供应商到 HQ 失败");
                return StatusCode(500, new { success = false, message = "同步到 HQ 失败" });
            }
        }

        public class SyncToHqRequest
        {
            public List<string> SupplierCodes { get; set; } = new();
        }

        [HttpPost]
        [Authorize(Policy = Permissions.AustralianSuppliers.Edit)]
        public async Task<IActionResult> Create([FromBody] CreateLocalSupplierDto dto)
        {
            var res = await _service.CreateAsync(dto);
            return Ok(new { success = res.Success, data = res.Data, message = res.Message });
        }

        [HttpPut("{code}")]
        [Authorize(Policy = Permissions.AustralianSuppliers.Edit)]
        public async Task<IActionResult> Update(string code, [FromBody] UpdateLocalSupplierDto dto)
        {
            var res = await _service.UpdateAsync(code, dto);
            return Ok(new { success = res.Success, data = res.Data, message = res.Message });
        }

        [HttpDelete("{code}")]
        [Authorize(Policy = Permissions.AustralianSuppliers.Edit)]
        public async Task<IActionResult> Delete(string code)
        {
            var res = await _service.DeleteAsync(code);
            return Ok(new { success = res.Success, message = res.Message });
        }

        [HttpPatch("{code}/status/{status}")]
        [Authorize(Policy = Permissions.AustralianSuppliers.Edit)]
        public async Task<IActionResult> ToggleStatus(string code, int status)
        {
            var res = await _service.ToggleStatusAsync(code, status);
            return Ok(new { success = res.Success, message = res.Message });
        }

        [HttpGet("check-code/{code}")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> CheckCode(string code)
        {
            var res = await _service.CheckCodeExistsAsync(code);
            return Ok(new { success = res.Success, data = res.Data, message = res.Message });
        }
    }
}
