using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/hq-containers")]
    [Authorize]
    // 旧接口同样返回货柜编号：无「查看完整货柜编号」权限的账号只拿到后 4 位。
    [MaskContainerNumbers]
    public class ReactHqContainerController : ControllerBase
    {
        private readonly IHqContainerReactService _service;
        private readonly ILogger<ReactHqContainerController> _logger;

        public ReactHqContainerController(
            IHqContainerReactService service,
            ILogger<ReactHqContainerController> logger
        )
        {
            _service = service;
            _logger = logger;
        }

        [HttpPost("list")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> List([FromBody] ContainerQueryRequest request)
        {
            try
            {
                var data = await _service.GetContainersAsync(request);
                return Ok(new { success = true, data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取HQ货柜列表失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpGet("{containerGuid}")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> Detail(string containerGuid)
        {
            try
            {
                var data = await _service.GetContainerDetailAsync(containerGuid);
                if (data == null)
                {
                    return NotFound(new { success = false, message = "货柜不存在" });
                }
                return Ok(new { success = true, data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取HQ货柜详情失败: {ContainerGuid}", containerGuid);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }
    }
}
