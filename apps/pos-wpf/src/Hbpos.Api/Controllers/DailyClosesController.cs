using System.Security.Claims;
using Hbpos.Api.Logging;
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
    IDailyCloseSyncService syncService,
    ILogger<DailyClosesController>? logger = null) : ControllerBase
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
            logger?.LogWarning(
                RejectedEvent(StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN"),
                "Daily close sync rejected status=403 code=DEVICE_SCOPE_FORBIDDEN store={StoreCode} device={DeviceCode} requestStore={RequestStoreCode} requestDevice={RequestDeviceCode} dailyClose={DailyCloseGuid}",
                storeCode,
                deviceCode,
                request.StoreCode,
                request.DeviceCode,
                request.DailyCloseGuid);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "DEVICE_SCOPE_FORBIDDEN",
                message = "Daily close scope does not match the authenticated device."
            });
        }

        try
        {
            var response = await syncService.SyncAsync(
                request,
                storeCode,
                deviceCode,
                cancellationToken);
            // 受理结果只进本地日志文件（Information 不进中心日志），用于核对补传历史日结、覆盖回填占位的过程。
            logger?.LogInformation(
                "Daily close sync accepted result={Result} store={StoreCode} device={DeviceCode} dailyClose={DailyCloseGuid} clientKind={ClientKind} businessDate={BusinessDate} appVersion={AppVersion}",
                response.ReplacedPlaceholder ? "ReplacedPlaceholder" : response.AlreadySynced ? "AlreadySynced" : "Inserted",
                storeCode,
                deviceCode,
                request.DailyCloseGuid,
                request.ClientKind,
                request.BusinessDate,
                request.AppVersion);
            return Ok(response);
        }
        catch (DailyCloseValidationException ex)
        {
            logger?.LogWarning(
                RejectedEvent(StatusCodes.Status400BadRequest, ex.Code),
                "Daily close sync rejected status=400 code={Code} store={StoreCode} device={DeviceCode} dailyClose={DailyCloseGuid} clientKind={ClientKind} appVersion={AppVersion} reason={Reason}",
                ex.Code,
                storeCode,
                deviceCode,
                request.DailyCloseGuid,
                request.ClientKind,
                request.AppVersion,
                ex.Message);
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
        catch (DailyCloseConflictException ex)
        {
            // 客户端把 409 当作永久拒绝、不再自动重试，所以必须在服务端留下「哪个字段不一致」。
            logger?.LogWarning(
                RejectedEvent(StatusCodes.Status409Conflict, ex.Code),
                "Daily close sync rejected status=409 code={Code} store={StoreCode} device={DeviceCode} dailyClose={DailyCloseGuid} clientKind={ClientKind} appVersion={AppVersion} detail={Detail}",
                ex.Code,
                storeCode,
                deviceCode,
                request.DailyCloseGuid,
                request.ClientKind,
                request.AppVersion,
                ex.Detail);
            return Conflict(new { code = ex.Code, message = ex.Message });
        }
    }

    private static EventId RejectedEvent(int statusCode, string code)
    {
        return RejectionEventIds.Create("DailyCloseSync", statusCode, code);
    }
}
