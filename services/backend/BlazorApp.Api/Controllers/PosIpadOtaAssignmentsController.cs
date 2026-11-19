using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers;

/// <summary>
/// iPad OTA 覆盖层（分店 / 设备）管理接口。基线 rollout 仍走 /api/pos-ipad/ota-rollout，
/// 这里的覆盖优先级高于基线：设备 → 分店 → 基线。
/// </summary>
[ApiController]
[Route("api/pos-ipad/ota-assignments")]
[Authorize]
public sealed class PosIpadOtaAssignmentsController(IPosIpadOtaPolicyService service)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.System.ViewAppDownloads)]
    public async Task<IActionResult> Get()
    {
        return Ok(await service.GetAssignmentsAsync());
    }

    [HttpGet("target-options/devices")]
    [Authorize(Policy = Permissions.System.ManageAppDownloads)]
    public async Task<IActionResult> GetTargetDeviceOptions(
        [FromQuery] string? storeGuid = null,
        [FromQuery] string? keyword = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20
    )
    {
        return Ok(await service.GetTargetDeviceOptionsAsync(storeGuid, keyword, page, pageSize));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.System.ManageAppDownloads)]
    public async Task<IActionResult> Create([FromBody] PosIpadOtaAssignmentRequest request)
    {
        var response = await service.CreateAssignmentAsync(
            request,
            User.Identity?.Name ?? "System"
        );
        return ToResult(response);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.System.ManageAppDownloads)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] PosIpadOtaAssignmentRequest request
    )
    {
        var response = await service.UpdateAssignmentAsync(
            id,
            request,
            User.Identity?.Name ?? "System"
        );
        return ToResult(response);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.System.ManageAppDownloads)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] long? expectedPolicyVersion
    )
    {
        var response = await service.DeleteAssignmentAsync(
            id,
            expectedPolicyVersion,
            User.Identity?.Name ?? "System"
        );
        return ToResult(response);
    }

    // 版本缺失 / 冲突 / 目标重复统一回 409，便于前端提示「刷新后重试」。
    private IActionResult ToResult<T>(ApiResponse<T> response) =>
        response.ErrorCode
            is AppUpdatePolicyErrorCodes.VersionRequired
                or AppUpdatePolicyErrorCodes.VersionConflict
                or "OTA_ASSIGNMENT_TARGET_EXISTS"
            ? Conflict(response)
            : Ok(response);
}
