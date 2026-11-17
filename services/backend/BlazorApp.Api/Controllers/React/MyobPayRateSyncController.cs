using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 从 MYOB 同步转账时薪（Web「工时与工资 → 个人时薪表」的「从 MYOB 获取时薪」抽屉）：预览 → 勾选 → 写入。
    /// 类级同时要求 Attendance.Payroll.Edit（写时薪）与 Myob.Employee.Bind（读 MYOB 员工卡）：两个 [Authorize] 是「且」的关系。
    /// 只读 MYOB，只往我方个人时薪表新增记录（只新增、不改历史）；响应不含 MYOB 员工 UID 与年薪，且 no-store。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/pay-rate-sync")]
    [Authorize(Policy = Permissions.Attendance.Payroll.Edit)]
    [Authorize(Policy = Permissions.Myob.Employee.Bind)]
    public class MyobPayRateSyncController : ControllerBase
    {
        private readonly IMyobPayRateSyncService _service;

        public MyobPayRateSyncController(IMyobPayRateSyncService service)
        {
            _service = service;
        }

        [HttpPost("preview")]
        public async Task<IActionResult> Preview(
            [FromBody] MyobPayRateSyncPreviewRequest request,
            CancellationToken cancellationToken)
        {
            Response.Headers.CacheControl = "no-store";
            return ToResult(await _service.PreviewAsync(request ?? new MyobPayRateSyncPreviewRequest(), cancellationToken));
        }

        [HttpPost("apply")]
        public async Task<IActionResult> Apply(
            [FromBody] MyobPayRateSyncApplyRequest request,
            CancellationToken cancellationToken)
        {
            Response.Headers.CacheControl = "no-store";
            return ToResult(await _service.ApplyAsync(request ?? new MyobPayRateSyncApplyRequest(), cancellationToken));
        }

        // 连接不存在 / 未启用返回 404，没有该分店权限返回 403，其余业务失败返回 400。
        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            if (result.ErrorCode is MyobErrorCodes.ConnectionNotFound or MyobErrorCodes.ConnectionNotActive)
            {
                return NotFound(result);
            }

            return result.ErrorCode is not null && result.ErrorCode.StartsWith("FORBIDDEN", StringComparison.Ordinal)
                ? StatusCode(StatusCodes.Status403Forbidden, result)
                : BadRequest(result);
        }
    }
}
