using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB Timesheet 推送（Web 页面 /pos-admin/myob「推送与对账」页签）。本批只有只读预览：不会往 MYOB 写任何东西。
    /// 权限沿用 Attendance.Payroll.Edit，并按当前账号可管理的分店过滤 / 拦截。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/timesheet")]
    [Authorize(Policy = Permissions.Attendance.Payroll.Edit)]
    public class MyobTimesheetController : ControllerBase
    {
        private readonly IMyobTimesheetPreviewService _service;

        public MyobTimesheetController(IMyobTimesheetPreviewService service)
        {
            _service = service;
        }

        /// <summary>某公司文件某工资周期的推送预览；periodStart 省略 = 上一个已结束的周期；checkMyob=true 才逐个员工读 MYOB 的 Timesheet 现状。</summary>
        [HttpGet("preview")]
        public async Task<IActionResult> Preview(
            [FromQuery] string connectionGuid,
            [FromQuery] string? periodStart,
            [FromQuery] bool checkMyob,
            CancellationToken cancellationToken)
        {
            var result = await _service.PreviewAsync(connectionGuid ?? string.Empty, periodStart, checkMyob, cancellationToken);
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode == MyobTimesheetErrorCodes.Forbidden
                ? StatusCode(StatusCodes.Status403Forbidden, result)
                : BadRequest(result);
        }
    }
}
