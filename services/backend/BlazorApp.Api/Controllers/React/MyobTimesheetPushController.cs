using System.Security.Claims;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB Timesheet 推送与撤销（Web 页面 /pos-admin/myob「推送与对账」页签）。**会写 MYOB 生产数据**：
    /// 权限 Myob.Timesheet.Push（默认不授予任何角色）；总闸 Myob__TimesheetPushEnabled 默认关；灰度白名单见 Myob__TimesheetPushConnectionGuids。
    /// 预览仍在 MyobTimesheetController（Attendance.Payroll.Edit）。契约与错误码见 MyobTimesheetPushDtos.cs。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/timesheet")]
    [Authorize(Policy = Permissions.Myob.Timesheet.Push)]
    public class MyobTimesheetPushController : ControllerBase
    {
        private readonly IMyobTimesheetPushService _service;

        public MyobTimesheetPushController(IMyobTimesheetPushService service)
        {
            _service = service;
        }

        /// <summary>推送一块（≤10 人）；同一次「点推送」的多块用同一个 batchGuid。客户端只传「谁」，工时由服务端重新规划。</summary>
        [HttpPost("push")]
        public async Task<IActionResult> Push([FromBody] MyobPushRequestDto request, CancellationToken cancellationToken) =>
            ToResult(await _service.PushAsync(request ?? new MyobPushRequestDto(), CurrentUserGuid(), cancellationToken));

        [HttpGet("batches")]
        public async Task<IActionResult> Batches(
            [FromQuery] string connectionGuid,
            [FromQuery] string? periodStart,
            CancellationToken cancellationToken) =>
            ToResult(await _service.ListBatchesAsync(connectionGuid ?? string.Empty, periodStart, cancellationToken));

        [HttpGet("batches/{batchGuid}")]
        public async Task<IActionResult> Batch(string batchGuid, CancellationToken cancellationToken) =>
            ToResult(await _service.GetBatchAsync(batchGuid, cancellationToken));

        /// <summary>撤销整个批次：把推送前的快照写回 MYOB；已发薪 / 被人改过 / 有更新批次的员工不动，逐个返回原因。</summary>
        [HttpPost("batches/{batchGuid}/undo")]
        public async Task<IActionResult> Undo(string batchGuid, CancellationToken cancellationToken) =>
            ToResult(await _service.UndoBatchAsync(batchGuid, CurrentUserGuid(), cancellationToken));

        private string CurrentUserGuid() =>
            User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            // 写 MYOB 的结果和批次明细都不该被缓存。
            Response.Headers.CacheControl = "no-store";
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode switch
            {
                MyobTimesheetErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
                MyobPushErrorCodes.Disabled => StatusCode(StatusCodes.Status403Forbidden, result),
                MyobPushErrorCodes.BatchNotFound or MyobErrorCodes.ConnectionNotFound or MyobErrorCodes.ConnectionNotActive => NotFound(result),
                MyobPushErrorCodes.InProgress => Conflict(result),
                _ => BadRequest(result),
            };
        }
    }
}
