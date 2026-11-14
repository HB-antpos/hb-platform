using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.PosSettings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

/// <summary>
/// 收银端设置下发：设备按约每分钟拉取总部为它所在门店 / 设备 / 全局设置的业务开关，应用后回执。
/// 授权：只要求设备认证，**不要求**收银员票据（应用启动、收银员尚未登录时同样要能同步，否则设置只会在有人登录后才生效）。
/// 门店与设备编号只取认证声明，不接受客户端指定。
/// </summary>
[ApiController]
[Route("api/v1/stores/current/pos-settings")]
[Authorize]
public sealed class PosSettingsController(IPosSettingsSyncService service) : ControllerBase
{
    [HttpGet("sync")]
    public async Task<ActionResult<ApiResult<PosSettingsSyncDto>>> Sync(
        [FromQuery] string? knownHash,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var deviceCode = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode) || string.IsNullOrWhiteSpace(deviceCode))
        {
            return Unauthorized(ApiResult<PosSettingsSyncDto>.Fail(
                "DEVICE_SCOPE_CLAIM_MISSING",
                "门店或设备认证声明缺失"));
        }

        var result = await service.GetSyncAsync(
            storeCode,
            deviceCode,
            User.FindFirstValue(DeviceAuthConstants.DeviceSystemClaim),
            knownHash,
            cancellationToken);
        return result.Sync is not null
            ? Ok(ApiResult<PosSettingsSyncDto>.Ok(result.Sync))
            : BadRequest(ApiResult<PosSettingsSyncDto>.Fail(
                result.ErrorCode ?? "POS_SETTINGS_INVALID",
                result.Message ?? "设置无效"));
    }

    [HttpPost("ack")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<ActionResult<ApiResult<PosSettingsAckResultDto>>> Ack(
        [FromBody] PosSettingsAckRequest request,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var deviceCode = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode) || string.IsNullOrWhiteSpace(deviceCode))
        {
            return Unauthorized(ApiResult<PosSettingsAckResultDto>.Fail(
                "DEVICE_SCOPE_CLAIM_MISSING",
                "门店或设备认证声明缺失"));
        }

        var result = await service.AckAsync(
            storeCode,
            deviceCode,
            User.FindFirstValue(DeviceAuthConstants.DeviceSystemClaim),
            request,
            cancellationToken);
        return result.Result is not null
            ? Ok(ApiResult<PosSettingsAckResultDto>.Ok(result.Result))
            : BadRequest(ApiResult<PosSettingsAckResultDto>.Fail(
                result.ErrorCode ?? "POS_SETTINGS_INVALID",
                result.Message ?? "回执无效"));
    }
}
