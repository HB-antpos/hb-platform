using BlazorApp.Api.Services.PosApiEntry;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 收银端 POS API 三条入口的用量统计（设备管理页「线路用量」视图）：按日 / 周 / 月汇总各入口的请求数、4xx、5xx 与占比，
    /// 并给出分店维度明细。只读，权限沿用 DeviceRegistration.View，不新增权限码；分店范围按当前用户可管理分店收敛（管理员全部）。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/pos-api-entry/usage")]
    [Authorize]
    public class PosApiEntryUsageController : ControllerBase
    {
        private readonly IPosApiEntryUsageService _service;

        public PosApiEntryUsageController(IPosApiEntryUsageService service)
        {
            _service = service;
        }

        /// <param name="startDate">起始日（含），yyyy-MM-dd，悉尼日期；缺省＝结束日往前 30 天。</param>
        /// <param name="endDate">结束日（含），yyyy-MM-dd，悉尼日期；缺省＝今天。</param>
        /// <param name="granularity">day（默认）/ week（周一起算）/ month（自然月）。</param>
        /// <param name="storeCode">可选，只看某家分店。</param>
        [HttpGet]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetUsage(
            [FromQuery] string? startDate,
            [FromQuery] string? endDate,
            [FromQuery] string? granularity,
            [FromQuery] string? storeCode,
            CancellationToken cancellationToken)
        {
            var result = await _service.GetUsageAsync(startDate, endDate, granularity, storeCode, cancellationToken);
            return result.Success ? Ok(result) : StatusCode(MapStatusCode(result.ErrorCode), result);
        }

        /// <summary>错误码到 HTTP 状态码：参数错误 400，越权 403。</summary>
        internal static int MapStatusCode(string? errorCode) =>
            errorCode switch
            {
                PosApiEntryErrorCodes.StoreForbidden or PosApiEntryErrorCodes.ScopeForbidden =>
                    StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest,
            };
    }
}
