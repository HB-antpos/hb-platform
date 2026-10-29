using System.Security.Claims;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 从 MYOB 同步资料到我方（Web 页面 /pos-admin/myob「员工绑定」页签的「从 MYOB 同步资料」抽屉）：
    /// 字段统计 → 差异预览 → 确认写入（留快照）→ 同步记录 / 7 天内撤销。
    /// 类级要求 Myob.Employee.Bind；写入类接口服务端另要求当前用户有 EmployeeProfiles.Edit（写员工资料用），没有则 403 语义的业务错误。
    /// 只读 MYOB，不会往 MYOB 写任何东西。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/employee-sync")]
    [Authorize(Policy = Permissions.Myob.Employee.Bind)]
    public class MyobEmployeeSyncController : ControllerBase
    {
        private readonly IMyobEmployeeSyncService _service;

        public MyobEmployeeSyncController(IMyobEmployeeSyncService service)
        {
            _service = service;
        }

        [HttpPost("field-stats")]
        public async Task<IActionResult> FieldStats(
            [FromBody] MyobSyncScopeRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.GetFieldStatsAsync(
                request ?? new MyobSyncScopeRequest(), CurrentUserGuid(), cancellationToken));

        [HttpPost("preview")]
        public async Task<IActionResult> Preview(
            [FromBody] MyobSyncPreviewRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.PreviewAsync(
                request ?? new MyobSyncPreviewRequest(), CurrentUserGuid(), cancellationToken));

        [HttpPost("apply")]
        public async Task<IActionResult> Apply(
            [FromBody] MyobSyncApplyRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.ApplyAsync(
                request ?? new MyobSyncApplyRequest(), CurrentUserGuid(), cancellationToken));

        [HttpGet("batches")]
        public async Task<IActionResult> Batches(
            [FromQuery] string connectionGuid,
            CancellationToken cancellationToken) =>
            ToResult(await _service.ListBatchesAsync(connectionGuid ?? string.Empty, cancellationToken));

        [HttpPost("batches/{batchGuid}/undo")]
        public async Task<IActionResult> Undo(string batchGuid, CancellationToken cancellationToken) =>
            ToResult(await _service.UndoAsync(batchGuid, CurrentUserGuid(), cancellationToken));

        private string CurrentUserGuid() =>
            User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        // 连接 / 同步记录不存在返回 404，没有写资料权限返回 403，其余业务失败返回 400。
        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode switch
            {
                MyobErrorCodes.ConnectionNotFound or MyobSyncErrorCodes.BatchNotFound => NotFound(result),
                MyobSyncErrorCodes.NoPermission => StatusCode(StatusCodes.Status403Forbidden, result),
                _ => BadRequest(result),
            };
        }
    }
}
