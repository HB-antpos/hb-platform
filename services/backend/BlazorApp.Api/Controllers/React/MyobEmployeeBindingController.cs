using System.Security.Claims;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB 员工绑定（Web 页面 /pos-admin/myob「员工绑定」页签）：拉取 MYOB 员工名册、打分建议、人工确认 / 解除绑定、忽略名单。
    /// 全部接口要求 Myob.Employee.Bind（默认不授予任何角色）。只读 MYOB，不会往 MYOB 写任何东西。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/employee-bindings")]
    [Authorize(Policy = Permissions.Myob.Employee.Bind)]
    public class MyobEmployeeBindingController : ControllerBase
    {
        private readonly IMyobEmployeeBindingService _service;

        public MyobEmployeeBindingController(IMyobEmployeeBindingService service)
        {
            _service = service;
        }

        /// <summary>某公司文件的完整绑定视图；refresh=true 绕过 10 分钟缓存重新拉 MYOB。</summary>
        [HttpGet]
        public async Task<IActionResult> GetSnapshot(
            [FromQuery] string connectionGuid,
            [FromQuery] bool refresh,
            CancellationToken cancellationToken) =>
            ToResult(await _service.GetSnapshotAsync(connectionGuid ?? string.Empty, refresh, cancellationToken));

        /// <summary>为某员工手动选卡：按得分降序的候选；keyword 命中姓名 / 邮箱 / 手机末几位。</summary>
        [HttpGet("candidates")]
        public async Task<IActionResult> GetCandidates(
            [FromQuery] string connectionGuid,
            [FromQuery] string userGuid,
            [FromQuery] string? keyword,
            CancellationToken cancellationToken) =>
            ToResult(await _service.GetCandidatesAsync(
                connectionGuid ?? string.Empty, userGuid ?? string.Empty, keyword, cancellationToken));

        [HttpPost("confirm")]
        public async Task<IActionResult> Confirm(
            [FromBody] MyobConfirmBindingsRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.ConfirmAsync(
                request ?? new MyobConfirmBindingsRequest(), CurrentUserGuid(), cancellationToken));

        [HttpPost("{linkGuid}/unbind")]
        public async Task<IActionResult> Unbind(string linkGuid, CancellationToken cancellationToken) =>
            ToResult(await _service.UnbindAsync(linkGuid, CurrentUserGuid(), cancellationToken));

        [HttpPost("ignore")]
        public async Task<IActionResult> Ignore(
            [FromBody] MyobIgnoreEmployeeRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.IgnoreAsync(
                request ?? new MyobIgnoreEmployeeRequest(), CurrentUserGuid(), cancellationToken));

        private string CurrentUserGuid() =>
            User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        // 连接 / 绑定记录不存在返回 404，其余业务失败返回 400。
        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode is MyobErrorCodes.ConnectionNotFound or MyobBindErrorCodes.LinkNotFound
                ? NotFound(result)
                : BadRequest(result);
        }
    }
}
