using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB Timesheet 写入探针（临时接口）：在配置指定的一个公司文件、一张 test test 专用测试员工卡上实测 PUT Timesheet 的真实行为，
    /// 方案 docs/plans/2026-10-29-myob-timesheet-push.md 第 3 节。配置（Myob__WriteProbeBusinessId /Myob__WriteProbeEmployeeDisplayId）
    /// 只在实测窗口内设置，未配置时本接口一律 404。权限沿用 Myob.Connection.Manage；实测完成后整个控制器与服务一并删除。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/timesheet/write-probe")]
    [Authorize(Policy = Permissions.Myob.Connection.Manage)]
    public class MyobWriteProbeController : ControllerBase
    {
        private readonly IMyobWriteProbeService _service;

        public MyobWriteProbeController(IMyobWriteProbeService service)
        {
            _service = service;
        }

        [HttpPost]
        public async Task<IActionResult> Run([FromBody] MyobWriteProbeRequest request, CancellationToken cancellationToken)
        {
            Response.Headers.CacheControl = "no-store";
            var result = await _service.RunAsync(request ?? new MyobWriteProbeRequest(), cancellationToken);
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode is MyobWriteProbeErrorCodes.NotEnabled or MyobErrorCodes.ConnectionNotFound or MyobErrorCodes.ConnectionNotActive
                ? NotFound(result)
                : BadRequest(result);
        }
    }
}
