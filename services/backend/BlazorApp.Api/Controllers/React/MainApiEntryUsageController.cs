using BlazorApp.Api.Services.MainApiEntry;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 主 API（Web 后台 + 员工 App）三条入口的用量统计（设备管理页「线路用量」视图的「主 API」来源）：
    /// 按日 / 周 / 月汇总各入口的请求数、4xx、5xx 与占比，并按客户端类别（web / app / other）拆分。
    /// 与收银端的 pos-api-entry/usage 并列、互不影响（响应结构不同，所以单开一个端点而不是给旧接口加参数）。
    /// 只读，权限沿用 DeviceRegistration.View，不新增权限码；没有分店维度，因此不做分店范围收敛。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/main-api-entry/usage")]
    [Authorize]
    public class MainApiEntryUsageController : ControllerBase
    {
        private readonly IMainApiEntryUsageService _service;

        public MainApiEntryUsageController(IMainApiEntryUsageService service)
        {
            _service = service;
        }

        /// <param name="startDate">起始日（含），yyyy-MM-dd，悉尼日期；缺省＝结束日往前 30 天。</param>
        /// <param name="endDate">结束日（含），yyyy-MM-dd，悉尼日期；缺省＝今天。</param>
        /// <param name="granularity">day（默认）/ week（周一起算）/ month（自然月）。</param>
        /// <param name="clientKind">可选，只看某类客户端：web / app / other。</param>
        [HttpGet]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetUsage(
            [FromQuery] string? startDate,
            [FromQuery] string? endDate,
            [FromQuery] string? granularity,
            [FromQuery] string? clientKind,
            CancellationToken cancellationToken)
        {
            var result = await _service.GetUsageAsync(startDate, endDate, granularity, clientKind, cancellationToken);
            return result.Success ? Ok(result) : BadRequest(result);
        }
    }
}
