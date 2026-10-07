using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/stores")]
[Authorize]
public sealed class StoresController(
    IStoreReceiptProfileService receiptProfileService,
    IStoreReceiptProfileReleaseService receiptProfileReleaseService) : ControllerBase
{
    [Authorize(Policy = CashierAuthorizationPolicies.ReceiptPrinter)]
    [HttpGet("current/receipt-profile")]
    public async Task<ActionResult<ApiResult<StoreReceiptProfileDto>>> GetCurrentReceiptProfile(
        CancellationToken cancellationToken)
    {
        // 门店代码只信任已验设备/收银员认证声明，绝不接受 query/body 任意分店。
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return Unauthorized(ApiResult<StoreReceiptProfileDto>.Fail(
                "STORE_CODE_CLAIM_MISSING",
                "门店代码认证声明缺失"));
        }

        var result = await receiptProfileService.GetCurrentAsync(storeCode, cancellationToken);
        if (result.Profile is not null)
        {
            return Ok(ApiResult<StoreReceiptProfileDto>.Ok(result.Profile));
        }

        return result.ErrorCode switch
        {
            StoreReceiptProfileService.StoreNotFoundCode => NotFound(
                ApiResult<StoreReceiptProfileDto>.Fail(
                    result.ErrorCode,
                    result.Message ?? "门店不存在或已停用")),
            _ => BadRequest(ApiResult<StoreReceiptProfileDto>.Fail(
                result.ErrorCode ?? "STORE_PROFILE_INVALID",
                result.Message ?? "门店资料无效"))
        };
    }

    /// <summary>
    /// 收银端按版本号轮询总部下发的小票资料：版本没变时只回 {changed:false}，响应很小，可每 60 秒调一次。
    /// </summary>
    [Authorize(Policy = CashierAuthorizationPolicies.ReceiptPrinter)]
    [HttpGet("current/receipt-profile/sync")]
    public async Task<ActionResult<ApiResult<StoreReceiptProfileSyncDto>>> SyncCurrentReceiptProfile(
        [FromQuery] string? knownVersion,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return Unauthorized(ApiResult<StoreReceiptProfileSyncDto>.Fail(
                "STORE_CODE_CLAIM_MISSING",
                "门店代码认证声明缺失"));
        }

        // 缺省、非数字或负数都按 0（本机从未应用过下发）处理，不让客户端的脏值变成 400。
        var known = int.TryParse(knownVersion, out var parsed) && parsed > 0 ? parsed : 0;
        var result = await receiptProfileReleaseService.GetSyncAsync(storeCode, known, cancellationToken);
        return result.Sync is not null
            ? Ok(ApiResult<StoreReceiptProfileSyncDto>.Ok(result.Sync))
            : BadRequest(ApiResult<StoreReceiptProfileSyncDto>.Fail(
                result.ErrorCode ?? "STORE_PROFILE_INVALID",
                result.Message ?? "门店资料无效"));
    }

    /// <summary>
    /// 收银端把总部下发的资料写入本机后回报「已应用到版本 N」，HBweb 据此显示每台设备的应用情况。
    /// 门店与设备都只取认证声明，不接受客户端指定。
    /// </summary>
    [Authorize(Policy = CashierAuthorizationPolicies.ReceiptPrinter)]
    [HttpPost("current/receipt-profile/ack")]
    [RequestSizeLimit(1024)]
    public async Task<ActionResult<ApiResult<StoreReceiptProfileAckResultDto>>> AckCurrentReceiptProfile(
        [FromBody] StoreReceiptProfileAckRequest request,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return Unauthorized(ApiResult<StoreReceiptProfileAckResultDto>.Fail(
                "STORE_CODE_CLAIM_MISSING",
                "门店代码认证声明缺失"));
        }

        var result = await receiptProfileReleaseService.AckAsync(
            storeCode,
            User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim) ?? string.Empty,
            User.FindFirstValue(DeviceAuthConstants.DeviceSystemClaim),
            request.Version,
            cancellationToken);
        if (result.Result is not null)
        {
            return Ok(ApiResult<StoreReceiptProfileAckResultDto>.Ok(result.Result));
        }

        var error = ApiResult<StoreReceiptProfileAckResultDto>.Fail(
            result.ErrorCode ?? "RECEIPT_PROFILE_ACK_FAILED",
            result.Message ?? "回执写入失败");
        return result.ErrorCode == StoreReceiptProfileReleaseService.DeviceCodeRequiredCode
            ? Unauthorized(error)
            : BadRequest(error);
    }
}
