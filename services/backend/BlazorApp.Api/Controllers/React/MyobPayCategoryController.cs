using System.Security.Claims;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// Timesheet 工资类别映射（Web 页面 /pos-admin/myob「连接与映射」页签）：读取 MYOB 工资类别、保存我方 5 档转账工时的对应关系。
    /// 属于公司文件配置，沿用 Myob.Connection.Manage。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/pay-categories")]
    [Authorize(Policy = Permissions.Myob.Connection.Manage)]
    public class MyobPayCategoryController : ControllerBase
    {
        private readonly IMyobPayCategoryService _service;

        public MyobPayCategoryController(IMyobPayCategoryService service)
        {
            _service = service;
        }

        /// <summary>某公司文件的 MYOB 工资类别 + 已保存映射 + 名称相近的建议；refresh=true 绕过 10 分钟缓存重新读 MYOB。</summary>
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] string connectionGuid,
            [FromQuery] bool refresh,
            CancellationToken cancellationToken) =>
            ToResult(await _service.GetAsync(connectionGuid ?? string.Empty, refresh, cancellationToken));

        [HttpPut("{connectionGuid}")]
        public async Task<IActionResult> Save(
            string connectionGuid,
            [FromBody] MyobPayCategoryMappingDto mapping,
            CancellationToken cancellationToken) =>
            ToResult(await _service.SaveAsync(
                connectionGuid, mapping ?? new MyobPayCategoryMappingDto(), CurrentUserGuid(), cancellationToken));

        private string CurrentUserGuid() =>
            User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        private IActionResult ToResult<T>(ApiResponse<T> result) =>
            result.Success ? Ok(result) : BadRequest(result);
    }
}
