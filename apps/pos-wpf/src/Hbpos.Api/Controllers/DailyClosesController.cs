using System.Security.Claims;
using Hbpos.Api.Services;
using Hbpos.Contracts.DailyClose;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

/// <summary>
/// 日结记录上传。只要求设备认证、不要求收银员票据：日结常在收银员会话过期后由后台补传，
/// 记录里的收银员信息是快照字段而不是授权依据。
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = DeviceAuthConstants.Scheme)]
[Route("api/v1/daily-closes")]
public sealed class DailyClosesController(
    IDailyCloseSyncService syncService) : ControllerBase
{
    internal const long MaximumRequestBytes = 256 * 1024;

    [HttpPost("sync")]
    [RequestSizeLimit(MaximumRequestBytes)]
    public async Task<ActionResult<DailyCloseSyncResponse>> Sync(
        [FromBody] DailyCloseSyncRequest request,
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
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "DEVICE_SCOPE_FORBIDDEN",
                message = "Daily close scope does not match the authenticated device."
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
        catch (DailyCloseValidationException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
        catch (DailyCloseConflictException ex)
        {
            return Conflict(new { code = ex.Code, message = ex.Message });
        }
    }
}
