using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.AppUpdates;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/app-updates/pos-ipad")]
[Authorize(AuthenticationSchemes = DeviceAuthConstants.Scheme)]
public sealed class PosIpadAppUpdateController : ControllerBase
{
    private readonly IOptions<PosIpadOptions> _options;
    private readonly IOptions<AppUpdateOptions> _appUpdateOptions;
    private readonly IPosIpadUpdateDecisionGateway? _gateway;

    // 生产 DI 必须选择同时接入中央策略和迁移开关的完整构造函数。
    [ActivatorUtilitiesConstructor]
    public PosIpadAppUpdateController(
        IOptions<PosIpadOptions> options,
        IOptions<AppUpdateOptions> appUpdateOptions,
        IPosIpadUpdateDecisionGateway gateway)
    {
        _options = options;
        _appUpdateOptions = appUpdateOptions;
        _gateway = gateway;
    }

    public PosIpadAppUpdateController(
        IOptions<PosIpadOptions> options,
        IPosIpadUpdateDecisionGateway gateway)
        : this(options, Options.Create(new AppUpdateOptions()), gateway)
    {
    }

    // 保留测试和迁移期的纯配置构造入口；生产 DI 始终使用中央策略网关。
    public PosIpadAppUpdateController(IOptions<PosIpadOptions> options)
    {
        _options = options;
        _appUpdateOptions = Options.Create(new AppUpdateOptions());
    }

    [NonAction]
    public ActionResult<ApiResult<PosIpadAppUpdateResponse>> Check(
        [FromQuery] string? version,
        [FromQuery] string? build,
        [FromQuery] string? runtimeVersion)
        => Ok(ApiResult<PosIpadAppUpdateResponse>.Ok(
            BuildLegacyResponse(version, build, runtimeVersion)));

    [HttpGet]
    public async Task<ActionResult<ApiResult<PosIpadAppUpdateResponse>>> Check(
        [FromQuery] string? version,
        [FromQuery] string? build,
        [FromQuery] string? runtimeVersion,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim)?.Trim();
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return Unauthorized(
                ApiResult<PosIpadAppUpdateResponse>.Fail(
                    "DEVICE_STORE_SCOPE_REQUIRED",
                    "Authenticated POS device store scope is required."));
        }

        if (!HasIpadDeviceSystem())
        {
            return Forbid();
        }

        // 迁移开关关闭时完全沿用本地策略，不能因中央尚未播种 none 而提前解除旧强制升级。
        if (!_appUpdateOptions.Value.CentralPolicyEnabled)
        {
            return Ok(ApiResult<PosIpadAppUpdateResponse>.Ok(
                BuildLegacyResponse(version, build, runtimeVersion)));
        }

        var decision = _gateway is null
            ? null
            : await _gateway.GetNativeDecisionAsync(
                new PosIpadNativeUpdateDecisionRequest(storeCode, version, build),
                cancellationToken);
        var response = decision is null
            ? BuildLegacyResponse(version, build, runtimeVersion)
            : MapNativeDecision(decision);

        return Ok(ApiResult<PosIpadAppUpdateResponse>.Ok(response));
    }

    [HttpGet("ota")]
    public async Task<ActionResult<ApiResult<PosIpadOtaUpdateResponse>>> CheckOta(
        [FromQuery] string? runtimeVersion,
        [FromQuery] string? currentUpdateId,
        [FromQuery] string? currentUpdateGroupId,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim)?.Trim();
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return Unauthorized(
                ApiResult<PosIpadOtaUpdateResponse>.Fail(
                    "DEVICE_STORE_SCOPE_REQUIRED",
                    "Authenticated POS device store scope is required."));
        }

        if (!HasIpadDeviceSystem())
        {
            return Forbid();
        }

        var decision = _gateway is null
            ? null
            : await _gateway.GetOtaDecisionAsync(
                new PosIpadOtaUpdateDecisionRequest(
                    storeCode,
                    runtimeVersion,
                    currentUpdateId,
                    currentUpdateGroupId,
                    User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim)?.Trim(),
                    User.FindFirstValue(DeviceAuthConstants.HardwareIdClaim)?.Trim()),
                cancellationToken);
        if (decision is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                ApiResult<PosIpadOtaUpdateResponse>.Fail(
                    "IPAD_OTA_DECISION_UNAVAILABLE",
                    "iPad OTA update decision is temporarily unavailable."));
        }

        return Ok(ApiResult<PosIpadOtaUpdateResponse>.Ok(decision));
    }

    private bool HasIpadDeviceSystem()
    {
        // 只信任认证 handler 签发且精确匹配的 iPadOS claim，缺失或其他平台均拒绝访问 iPad 更新路由。
        return string.Equals(
            User.FindFirstValue(DeviceAuthConstants.DeviceSystemClaim),
            DeviceSystems.IpadOs,
            StringComparison.Ordinal);
    }

    private PosIpadAppUpdateResponse BuildLegacyResponse(
        string? version,
        string? build,
        string? runtimeVersion)
    {
        var configuration = _options.Value;
        return new PosIpadAppUpdateResponse(
            // 复用既有六字段合同；enabled 只控制开始新交易，不影响恢复和补传。
            Enabled: AllowsTransactions(),
            configuration.MinimumSupportedVersion,
            configuration.LatestVersion,
            PosIpadAppVersionPolicy.IsForceUpdateRequired(
                configuration,
                version,
                build,
                runtimeVersion),
            configuration.AppStoreUrl,
            configuration.ReleaseMessage);
    }

    private PosIpadAppUpdateResponse MapNativeDecision(
        PosIpadNativeUpdateDecision decision) =>
        new(
            Enabled: AllowsTransactions(),
            decision.MinimumSupportedVersion,
            decision.LatestVersion,
            ForceUpdate: string.Equals(
                decision.State,
                "required",
                StringComparison.Ordinal),
            decision.AppStoreUrl,
            decision.ReleaseMessage);

    private bool AllowsTransactions()
    {
        var claimValue = HttpContext?.User.FindFirstValue(
            DeviceAuthConstants.AllowTransactionsClaim);
        // 旧认证票据没有该 claim 时默认允许，避免升级窗口误禁用新交易。
        return !bool.TryParse(claimValue, out var allowed) || allowed;
    }
}
