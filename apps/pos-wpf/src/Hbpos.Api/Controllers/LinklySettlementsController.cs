using System.Security.Claims;
using Hbpos.Api.Logging;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = DeviceAuthConstants.Scheme)]
[Route("api/v1/linkly/settlements")]
public sealed class LinklySettlementsController(
    ILinklySettlementSyncService syncService,
    ILogger<LinklySettlementsController>? logger = null) : ControllerBase
{
    internal const long MaximumRequestBytes = 1024 * 1024;

    [HttpPost("sync")]
    [RequestSizeLimit(MaximumRequestBytes)]
    public async Task<ActionResult<LinklySettlementSyncResponse>> Sync(
        [FromBody] LinklySettlementSyncRequest request,
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

        if (!string.Equals(request.StoreCode, storeCode, StringComparison.Ordinal) ||
            !string.Equals(request.DeviceCode, deviceCode, StringComparison.Ordinal))
        {
            // 上传范围只信任认证 claims；拒绝静默改写，避免错误设备的离线记录落入当前 scope。
            logger?.LogWarning(
                RejectedEvent(StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN"),
                "Linkly settlement sync rejected status=403 code=DEVICE_SCOPE_FORBIDDEN store={StoreCode} device={DeviceCode} requestStore={RequestStoreCode} requestDevice={RequestDeviceCode} settlement={SettlementGuid}",
                storeCode,
                deviceCode,
                request.StoreCode,
                request.DeviceCode,
                request.SettlementGuid);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "DEVICE_SCOPE_FORBIDDEN",
                message = "Settlement scope does not match the authenticated device."
            });
        }

        try
        {
            return Ok(await syncService.SyncAsync(
                request,
                storeCode,
                deviceCode,
                cancellationToken));
        }
        catch (LinklySettlementValidationException ex)
        {
            logger?.LogWarning(
                RejectedEvent(StatusCodes.Status400BadRequest, ex.Code),
                "Linkly settlement sync rejected status=400 code={Code} store={StoreCode} device={DeviceCode} settlement={SettlementGuid} revision={ClientRevision} reason={Reason}",
                ex.Code,
                storeCode,
                deviceCode,
                request.SettlementGuid,
                request.ClientRevision,
                ex.Message);
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
        catch (LinklySettlementConflictException ex)
        {
            // 客户端收到 409 多半会把记录标成永久拒绝、不再自动重试，所以必须在服务端留下「哪个字段不一致」。
            logger?.LogWarning(
                RejectedEvent(StatusCodes.Status409Conflict, ex.Code),
                "Linkly settlement sync rejected status=409 code={Code} store={StoreCode} device={DeviceCode} settlement={SettlementGuid} revision={ClientRevision} detail={Detail}",
                ex.Code,
                storeCode,
                deviceCode,
                request.SettlementGuid,
                request.ClientRevision,
                ex.Detail);
            return Conflict(new { code = ex.Code, message = ex.Message });
        }
    }

    private static EventId RejectedEvent(int statusCode, string code)
    {
        return RejectionEventIds.Create("LinklySettlementSync", statusCode, code);
    }
}
