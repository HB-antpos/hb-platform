using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using BlazorApp.Shared.Options;
using System.Security.Claims;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/devices")]
public sealed class DevicesController(
    IDeviceService deviceService,
    IOptions<DeviceActivationOptions>? deviceActivationOptions = null) : ControllerBase
{
    // AppVersion 可选：旧版客户端不带该字段，服务端保留库里上次上报的版本，不会被清空。
    public sealed record DeviceRuntimeStatusRequest(
        bool IsOnline,
        string? CurrentCashierId,
        string? CurrentCashierName,
        string? AppVersion = null);

    [AllowAnonymous]
    [HttpPost("register")]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<ApiResult<DeviceRegisterResponse>>> Register(
        [FromBody] DeviceRegisterRequest request,
        CancellationToken cancellationToken)
    {
        if (DeviceSystems.TryNormalize(request.DeviceSystem, out var deviceSystem)
            && !(deviceActivationOptions?.Value ?? new DeviceActivationOptions())
                .IsLegacyRegistrationEnabled(deviceSystem))
        {
            // 关闭某平台的旧注册时必须在调用 service 前拒绝，确保零数据库写入。
            var denied = new DeviceRegisterResponse(
                string.Empty,
                request.StoreCode?.Trim() ?? string.Empty,
                string.Empty,
                3,
                false,
                "A one-time device activation code is required.",
                null,
                DeviceActivationReasonCodes.ActivationCodeRequired);
            return Ok(ApiResult<DeviceRegisterResponse>.Ok(denied));
        }

        // 普通注册端点永不接受审核开通码，避免绕过审核专用限流路径。
        var response = await deviceService.RegisterAsync(
            request with { ProvisioningCode = null },
            cancellationToken);
        return Ok(ApiResult<DeviceRegisterResponse>.Ok(response));
    }

    [AllowAnonymous]
    [EnableRateLimiting("app-review-device-registration")]
    [HttpPost("app-review-register")]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<ApiResult<DeviceRegisterResponse>>> AppReviewRegister(
        [FromBody] DeviceRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var response = await deviceService.RegisterForAppReviewAsync(request, cancellationToken);
        return Ok(ApiResult<DeviceRegisterResponse>.Ok(response));
    }

    [AllowAnonymous]
    [HttpPost("verify")]
    public async Task<ActionResult<ApiResult<DeviceVerifyResponse>>> Verify(
        [FromBody] DeviceVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var response = await deviceService.VerifyAsync(request, cancellationToken);
        return Ok(ApiResult<DeviceVerifyResponse>.Ok(response));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.DeviceRegistration)]
    [HttpPost("reregister")]
    public async Task<ActionResult<ApiResult<DeviceReregisterResponse>>> Reregister(
        [FromBody] DeviceReregisterRequest request,
        CancellationToken cancellationToken)
    {
        var deviceCode = User?.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        var storeCode = User?.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var hardwareId = User?.FindFirstValue(DeviceAuthConstants.HardwareIdClaim);
        var deviceSystem = User?.FindFirstValue(DeviceAuthConstants.DeviceSystemClaim);
        if (string.IsNullOrWhiteSpace(deviceCode)
            || string.IsNullOrWhiteSpace(storeCode)
            || string.IsNullOrWhiteSpace(hardwareId))
        {
            return Unauthorized(ApiResult<DeviceReregisterResponse>.Fail(
                "DEVICE_AUTH_REQUIRED",
                "Device authorization is required."));
        }

        var response = await deviceService.ReregisterAsync(
            request,
            new DeviceReregisterContext(deviceCode, storeCode, hardwareId, deviceSystem ?? DeviceSystems.Windows),
            cancellationToken);
        return Ok(ApiResult<DeviceReregisterResponse>.Ok(response));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.DeviceRegistrationReset)]
    [HttpPost("reset-registration")]
    [RequestSizeLimit(1024)]
    public async Task<ActionResult<ApiResult<DeviceRegistrationResetResponse>>> ResetRegistration(
        [FromBody] DeviceRegistrationResetRequest request,
        CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty)
        {
            return BadRequest(ApiResult<DeviceRegistrationResetResponse>.Fail(
                "OPERATION_ID_REQUIRED",
                "operationId is required."));
        }

        var deviceCode = User?.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        var storeCode = User?.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var hardwareId = User?.FindFirstValue(DeviceAuthConstants.HardwareIdClaim);
        if (string.IsNullOrWhiteSpace(deviceCode)
            || string.IsNullOrWhiteSpace(storeCode)
            || string.IsNullOrWhiteSpace(hardwareId))
        {
            return Unauthorized(ApiResult<DeviceRegistrationResetResponse>.Fail(
                "DEVICE_AUTH_REQUIRED",
                "Device authorization is required."));
        }

        var cashierId = HttpContext.Items[CashierAuthorizationContext.CashierIdItemKey] as string;
        if (string.IsNullOrWhiteSpace(cashierId))
        {
            return Unauthorized(ApiResult<DeviceRegistrationResetResponse>.Fail(
                "CASHIER_AUTH_REQUIRED",
                "Fresh cashier authorization is required."));
        }

        var response = await deviceService.ResetRegistrationAsync(
            request,
            new DeviceRegistrationResetContext(deviceCode, storeCode, hardwareId, cashierId),
            cancellationToken);
        return Ok(ApiResult<DeviceRegistrationResetResponse>.Ok(response));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.DeviceRegistration)]
    [HttpPost("runtime-status")]
    public async Task<ActionResult<ApiResult<object>>> ReportRuntimeStatus(
        [FromBody] DeviceRuntimeStatusRequest request,
        CancellationToken cancellationToken)
    {
        var hardwareId = User?.FindFirstValue(DeviceAuthConstants.HardwareIdClaim);
        var deviceCode = User?.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        var storeCode = User?.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        if (string.IsNullOrWhiteSpace(hardwareId)
            || string.IsNullOrWhiteSpace(deviceCode)
            || string.IsNullOrWhiteSpace(storeCode))
        {
            return Unauthorized(ApiResult<object>.Fail(
                "DEVICE_AUTH_REQUIRED",
                "Device authorization is required."));
        }

        var updated = await deviceService.UpdateRuntimeStatusAsync(
            hardwareId,
            deviceCode,
            storeCode,
            request.IsOnline,
            request.CurrentCashierId,
            request.CurrentCashierName,
            request.AppVersion,
            cancellationToken);
        if (!updated)
        {
            return NotFound(ApiResult<object>.Fail("DEVICE_NOT_FOUND", "Device was not found."));
        }

        return Ok(ApiResult<object>.Ok(new { }));
    }
}
