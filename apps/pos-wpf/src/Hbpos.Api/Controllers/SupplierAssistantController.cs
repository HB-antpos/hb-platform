using System.Security.Claims;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.SupplierAssistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

/// <summary>
/// 大屏收银 App 内「订货助手」：设备绑定哪家分店，就直接用那家分店的订货助手功能，不需要登录 HB 网站账号。
///
/// 中文注释：
/// 1. 只认设备认证（授权码 + 设备号 + 分店 + 硬件码），不要求收银员登录。
/// 2. 只给大屏收银 App（iPadOS、安卓平板）；WPF、手持端一律拒绝。
/// 3. 分店取自设备认证写入的声明，App 无法指定。
/// </summary>
[ApiController]
[Route("api/v1/supplier-assistant")]
[Authorize(AuthenticationSchemes = DeviceAuthConstants.Scheme)]
public sealed class SupplierAssistantController(ISupplierAssistantTokenGateway gateway) : ControllerBase
{
    private static readonly HashSet<string> SupportedDeviceSystems = new(
        [DeviceSystems.IpadOs, DeviceSystems.AndroidTablet],
        StringComparer.Ordinal);

    [HttpPost("token")]
    public async Task<ActionResult<ApiResult<SupplierAssistantTokenResponse>>> IssueToken(
        CancellationToken cancellationToken)
    {
        var deviceCode = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var hardwareId = User.FindFirstValue(DeviceAuthConstants.HardwareIdClaim);
        var deviceSystem = User.FindFirstValue(DeviceAuthConstants.DeviceSystemClaim);
        if (string.IsNullOrWhiteSpace(deviceCode)
            || string.IsNullOrWhiteSpace(storeCode)
            || string.IsNullOrWhiteSpace(hardwareId))
        {
            return Unauthorized(ApiResult<SupplierAssistantTokenResponse>.Fail(
                "DEVICE_AUTH_REQUIRED",
                "Device authorization is required."));
        }

        if (deviceSystem is null || !SupportedDeviceSystems.Contains(deviceSystem))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResult<SupplierAssistantTokenResponse>.Fail(
                    SupplierAssistantErrorCodes.DeviceSystemNotSupported,
                    "Supplier assistant is only available on the HB POS tablet app."));
        }

        var result = await gateway.IssueAsync(
            new SupplierAssistantDeviceIdentity(deviceCode, storeCode, hardwareId),
            cancellationToken);
        return result.Outcome switch
        {
            SupplierAssistantTokenOutcome.Issued when result.Token is { } token => Ok(
                ApiResult<SupplierAssistantTokenResponse>.Ok(new SupplierAssistantTokenResponse(
                    token.AccessToken,
                    token.ExpiresAtUtc,
                    token.StoreCode,
                    token.DeviceCode,
                    token.ApiOrigin))),
            SupplierAssistantTokenOutcome.NotEligible => StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResult<SupplierAssistantTokenResponse>.Fail(
                    SupplierAssistantErrorCodes.DeviceNotEligible,
                    "This device is not enabled for the supplier assistant.")),
            _ => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                ApiResult<SupplierAssistantTokenResponse>.Fail(
                    SupplierAssistantErrorCodes.Unavailable,
                    "Supplier assistant is temporarily unavailable.")),
        };
    }
}
