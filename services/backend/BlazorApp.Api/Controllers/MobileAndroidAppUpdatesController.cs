using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers;

/// <summary>
/// Mobile 安卓原生最低构建号的匿名判定入口。App 在登录前就要调用，所以不能要求登录。
/// </summary>
[ApiController]
[Route("api/app-updates/mobile-android")]
public sealed class MobileAndroidAppUpdatesController(
    IMobileAndroidNativeUpdatePolicyService service
) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Check([FromQuery] string? build)
    {
        // build 缺失或非法时服务返回 none（fail-open），这里不做 400 拦截。
        var decision = await service.GetDecisionAsync(build);
        return Ok(ApiResponse<MobileAndroidNativeUpdateDecisionDto>.OK(decision));
    }
}
