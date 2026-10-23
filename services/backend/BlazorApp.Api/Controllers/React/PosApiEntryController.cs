using BlazorApp.Api.Services;
using BlazorApp.Api.Services.PosApiEntry;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 收银端 POS API 入口（EdgeOne / AWS 悉尼 / 直连）按分店设置：GET 看每家有收银设备的分店的首选入口与设备实际入口统计，
    /// PUT 批量修改首选入口。放在设备管理页的「收银入口」视图下，复用 DeviceRegistration.View / Manage，不新增权限码。
    /// 分店范围按当前用户可管理分店收敛（管理员全部）。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/pos-api-entry")]
    [Authorize]
    public class PosApiEntryController : ControllerBase
    {
        private readonly IPosApiEntryService _service;
        private readonly ICurrentUserService _currentUserService;

        public PosApiEntryController(IPosApiEntryService service, ICurrentUserService currentUserService)
        {
            _service = service;
            _currentUserService = currentUserService;
        }

        [HttpGet("stores")]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetStores(CancellationToken cancellationToken)
        {
            var result = await _service.GetStoresAsync(cancellationToken);
            return ToActionResult(result);
        }

        [HttpPut("stores")]
        [Authorize(Policy = Permissions.DeviceRegistration.Manage)]
        public async Task<IActionResult> UpdateStores(
            [FromBody] PosApiEntryUpdateRequestDto? request,
            CancellationToken cancellationToken)
        {
            var result = await _service.UpdateAsync(
                request,
                _currentUserService.GetCurrentUsername(),
                cancellationToken);
            return ToActionResult(result);
        }

        private IActionResult ToActionResult<T>(ApiResponse<T> result) =>
            result.Success ? Ok(result) : StatusCode(MapStatusCode(result.ErrorCode), result);

        /// <summary>错误码到 HTTP 状态码：参数 / mode 错误 400，越权 403，分店不存在 404。</summary>
        internal static int MapStatusCode(string? errorCode) =>
            errorCode switch
            {
                PosApiEntryErrorCodes.StoreForbidden or PosApiEntryErrorCodes.ScopeForbidden =>
                    StatusCodes.Status403Forbidden,
                PosApiEntryErrorCodes.StoreNotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest,
            };
    }
}
