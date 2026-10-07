using BlazorApp.Api.Services;
using BlazorApp.Api.Services.StoreReceiptProfiles;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 门店小票资料「下发」：status 看每家店新旧对比与设备应用情况，devices 看单店设备明细，publish 生成新版本快照。
    /// 路由前缀 api/stores/receipt-profile 与现有 StoresController（api/Stores/guid/...）不冲突；
    /// 复用 Stores.View / Stores.Edit，不新增权限码（新权限码还要手工入库）。
    /// </summary>
    [ApiController]
    [Route("api/stores/receipt-profile")]
    [Authorize]
    public class StoreReceiptProfilesController : ControllerBase
    {
        private readonly IStoreReceiptProfileService _service;
        private readonly ICurrentUserService _currentUserService;

        public StoreReceiptProfilesController(
            IStoreReceiptProfileService service,
            ICurrentUserService currentUserService
        )
        {
            _service = service;
            _currentUserService = currentUserService;
        }

        [HttpPost("status")]
        [Authorize(Policy = Permissions.Stores.View)]
        public async Task<IActionResult> GetStatus(
            [FromBody] StoreReceiptProfileRequestDto? request,
            CancellationToken cancellationToken
        )
        {
            var result = await _service.GetStatusAsync(request?.StoreGuids, cancellationToken);
            return ToActionResult(result);
        }

        [HttpGet("{storeGuid}/devices")]
        [Authorize(Policy = Permissions.Stores.View)]
        public async Task<IActionResult> GetDevices(
            string storeGuid,
            CancellationToken cancellationToken
        )
        {
            var result = await _service.GetDevicesAsync(storeGuid, cancellationToken);
            return ToActionResult(result);
        }

        [HttpPost("publish")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> Publish(
            [FromBody] StoreReceiptProfileRequestDto? request,
            CancellationToken cancellationToken
        )
        {
            var result = await _service.PublishAsync(
                request?.StoreGuids,
                _currentUserService.GetCurrentUsername(),
                cancellationToken
            );
            return ToActionResult(result);
        }

        private IActionResult ToActionResult<T>(ApiResponse<T> result) =>
            result.Success ? Ok(result) : StatusCode(MapStatusCode(result.ErrorCode), result);

        /// <summary>错误码到 HTTP 状态码：参数错误与整批不可下发 400，门店不存在 404，并发冲突 409。</summary>
        internal static int MapStatusCode(string? errorCode) =>
            errorCode switch
            {
                StoreReceiptProfileErrorCodes.StoreNotFound => StatusCodes.Status404NotFound,
                StoreReceiptProfileErrorCodes.PublishConflict => StatusCodes.Status409Conflict,
                StoreReceiptProfileErrorCodes.InvalidRequest
                or StoreReceiptProfileErrorCodes.NotPublishable => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status400BadRequest,
            };
    }
}
