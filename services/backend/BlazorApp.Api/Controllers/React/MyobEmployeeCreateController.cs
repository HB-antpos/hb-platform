using System.Security.Claims;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB「员工绑定 → 对方没有」里的「我方建档」：按 MYOB 员工卡预填资料，人工核对后建我方账号并绑定这张卡。
    /// 两个策略同时要求（类上两个 Authorize 取交集）：Myob.Employee.Bind，以及建门店员工的 Users.CreateStoreStaff（Users.Create 隐含它）。
    /// 草稿接口会返回 MYOB 里的完整电话，所以同样要求建员工权限。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/employee-create")]
    [Authorize(Policy = Permissions.Myob.Employee.Bind)]
    [Authorize(Policy = Permissions.Users.CreateStoreStaff)]
    public class MyobEmployeeCreateController : ControllerBase
    {
        private readonly IMyobEmployeeCreateService _service;

        public MyobEmployeeCreateController(IMyobEmployeeCreateService service)
        {
            _service = service;
        }

        [HttpGet("draft")]
        public async Task<IActionResult> GetDraft(
            [FromQuery] string connectionGuid,
            [FromQuery] string myobEmployeeUid,
            CancellationToken cancellationToken) =>
            ToResult(await _service.GetDraftAsync(connectionGuid ?? string.Empty, myobEmployeeUid ?? string.Empty, cancellationToken));

        [HttpPost]
        public async Task<IActionResult> Create(
            [FromBody] MyobCreateFromMyobRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.CreateAsync(
                request ?? new MyobCreateFromMyobRequest(),
                CurrentUserGuid(),
                User.Identity?.Name ?? "system",
                cancellationToken));

        private string CurrentUserGuid() =>
            User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        // 连接不存在返回 404，越权 403，其余业务失败 400（与员工绑定接口一致）。
        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode switch
            {
                MyobErrorCodes.ConnectionNotFound => NotFound(result),
                MyobCreateErrorCodes.Forbidden or "FORBIDDEN" => StatusCode(403, result),
                _ => BadRequest(result),
            };
        }
    }
}
