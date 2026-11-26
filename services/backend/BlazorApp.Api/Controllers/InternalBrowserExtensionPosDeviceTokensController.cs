using BlazorApp.Api.Authentication;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers;

public sealed record PosDeviceSupplierAssistantTokenRequest(
    string? DeviceCode,
    string? StoreCode,
    string? HardwareId
);

public sealed record PosDeviceSupplierAssistantTokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiry,
    string StoreCode,
    string DeviceCode
);

/// <summary>
/// 仅供 POS API 调用：POS API 已用设备授权码核实身份后，凭专用 service token 来这里换设备级订货助手令牌。
///
/// 中文注释：主 API 不接收也不校验设备授权码（那只在 POS API 一处校验），
/// 但仍按「设备号 + 分店 + 硬件识别码」重新核对登记记录，设备停用或换店时直接拒绝。
/// </summary>
[ApiController]
[Route("api/internal/browser-extension/pos-device-tokens")]
[Authorize(
    AuthenticationSchemes = ServiceApiTokenAuthenticationDefaults.AuthenticationScheme,
    Policy = ServiceApiScopes.IssuePosDeviceSupplierAssistantTokens
)]
public sealed class InternalBrowserExtensionPosDeviceTokensController(
    IBrowserExtensionPosDeviceDirectory directory,
    IBrowserExtensionPosDeviceTokenIssuer issuer,
    ILogger<InternalBrowserExtensionPosDeviceTokensController> logger
) : ControllerBase
{
    public const string DeviceNotEligibleErrorCode = "POS_DEVICE_NOT_ELIGIBLE";

    [HttpPost]
    public async Task<IActionResult> Issue(
        [FromBody] PosDeviceSupplierAssistantTokenRequest? request,
        CancellationToken cancellationToken
    )
    {
        var identity = await directory.FindActiveAsync(
            request?.DeviceCode ?? string.Empty,
            request?.StoreCode ?? string.Empty,
            request?.HardwareId ?? string.Empty,
            cancellationToken
        );
        if (identity == null)
        {
            logger.LogWarning(
                "订货助手设备令牌拒绝签发 DeviceCode={DeviceCode} StoreCode={StoreCode}",
                request?.DeviceCode,
                request?.StoreCode
            );
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<PosDeviceSupplierAssistantTokenResponse>.Error(
                    "设备未启用、未登记到该分店，或不是大屏收银 App",
                    DeviceNotEligibleErrorCode
                )
            );
        }

        var token = issuer.Issue(identity);
        return Ok(
            ApiResponse<PosDeviceSupplierAssistantTokenResponse>.OK(
                new PosDeviceSupplierAssistantTokenResponse(
                    token.AccessToken,
                    token.ExpiresAtUtc,
                    identity.StoreCode,
                    identity.DeviceCode
                )
            )
        );
    }
}
