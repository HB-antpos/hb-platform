using System.Security.Claims;
using Hbpos.Api.Logging;
using Hbpos.Api.Services;
using Hbpos.Contracts.CashMovements;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

/// <summary>
/// 收银端现金存入 / 取出明细上传。与日结一样只要求设备认证：记录在本地先落库、由后台补传，
/// 收银员与授权人是快照字段，权限已在收银端操作时校验。
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = DeviceAuthConstants.Scheme)]
[Route("api/v1/cash-movements")]
public sealed class CashMovementsController(
    ICashMovementSyncService syncService,
    ILogger<CashMovementsController>? logger = null) : ControllerBase
{
    internal const long MaximumRequestBytes = 256 * 1024;

    [HttpPost("sync")]
    [RequestSizeLimit(MaximumRequestBytes)]
    public async Task<ActionResult<CashMovementSyncResponse>> Sync(
        [FromBody] CashMovementSyncRequest request,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var deviceCode = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        if (User.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(storeCode) ||
            string.IsNullOrWhiteSpace(deviceCode))
        {
            return Unauthorized(new
            {
                code = "DEVICE_AUTH_REQUIRED",
                message = "Authenticated device scope claims are required."
            });
        }

        // 上传范围只信任认证 claims，拒绝静默改写，避免错误设备的离线记录落入当前 scope。
        if (!string.Equals(request.StoreCode, storeCode, StringComparison.Ordinal) ||
            !string.Equals(request.DeviceCode, deviceCode, StringComparison.Ordinal))
        {
            logger?.LogWarning(
                RejectedEvent(StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN"),
                "Cash movement sync rejected status=403 code=DEVICE_SCOPE_FORBIDDEN store={StoreCode} device={DeviceCode} requestStore={RequestStoreCode} requestDevice={RequestDeviceCode}",
                storeCode,
                deviceCode,
                request.StoreCode,
                request.DeviceCode);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "DEVICE_SCOPE_FORBIDDEN",
                message = "Cash movement scope does not match the authenticated device."
            });
        }

        try
        {
            var response = await syncService.SyncAsync(request, storeCode, deviceCode, cancellationToken);
            foreach (var rejected in response.Results.Where(item =>
                         item.Status == CashMovementSyncStatuses.Rejected))
            {
                // 单条拒绝客户端不再重试，必须在服务端留下原因。
                logger?.LogWarning(
                    RejectedEvent(StatusCodes.Status200OK, rejected.Code ?? "REJECTED"),
                    "Cash movement rejected code={Code} store={StoreCode} device={DeviceCode} movement={MovementGuid} appVersion={AppVersion} reason={Reason}",
                    rejected.Code,
                    storeCode,
                    deviceCode,
                    rejected.MovementGuid,
                    request.AppVersion,
                    rejected.Message);
            }

            return Ok(response);
        }
        catch (DailyCloseValidationException ex)
        {
            logger?.LogWarning(
                RejectedEvent(StatusCodes.Status400BadRequest, ex.Code),
                "Cash movement sync rejected status=400 code={Code} store={StoreCode} device={DeviceCode} appVersion={AppVersion} reason={Reason}",
                ex.Code,
                storeCode,
                deviceCode,
                request.AppVersion,
                ex.Message);
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    private static EventId RejectedEvent(int statusCode, string code)
    {
        return RejectionEventIds.Create("CashMovementSync", statusCode, code);
    }
}
