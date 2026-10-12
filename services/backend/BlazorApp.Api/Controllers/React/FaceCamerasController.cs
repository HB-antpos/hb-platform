using System.Security.Claims;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 门店人脸抓拍摄像机管理（Web 后台 /system/face-cameras）。
/// 权限：整个控制器只认 <see cref="Permissions.FaceCapture.Cameras.Manage"/>（Admin 角色靠角色放行）。
/// 所有响应都带 Cache-Control: no-store：新增 / 轮换的响应里有一次性 token，不允许被浏览器或代理缓存。
/// 没有删除接口，摄像机只能停用。
/// </summary>
[ApiController]
[Route("api/react/v1/face-cameras")]
[Authorize(Policy = Permissions.FaceCapture.Cameras.Manage)]
[FaceCameraNoStore]
public sealed class FaceCamerasController(IFaceCameraAdminService service) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List(CancellationToken cancellationToken) =>
        RunAsync(() => service.ListAsync(cancellationToken));

    /// <summary>新增摄像机；响应里的 token 与完整地址只此一次，库里只存哈希。</summary>
    [HttpPost]
    public Task<IActionResult> Create([FromBody] FaceCameraCreateRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => service.CreateAsync(request ?? new(), ResolveActor(), cancellationToken));

    [HttpPut("{cameraId}")]
    public Task<IActionResult> Update(
        string cameraId, [FromBody] FaceCameraUpdateRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => service.UpdateAsync(cameraId, request ?? new(), ResolveActor(), cancellationToken));

    /// <summary>生成新 token 并替换哈希；旧 token 在人脸接收服务下一次刷新（最多 30 秒）后失效。</summary>
    [HttpPost("{cameraId}/rotate-token")]
    public Task<IActionResult> RotateToken(string cameraId, CancellationToken cancellationToken) =>
        RunAsync(() => service.RotateTokenAsync(cameraId, ResolveActor(), cancellationToken));

    /// <summary>最近抓拍记录（仅元数据，不含任何图片或图片地址）。</summary>
    [HttpGet("{cameraId}/captures")]
    public Task<IActionResult> Captures(
        string cameraId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = FaceCameraAdminService.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => service.ListCapturesAsync(cameraId, page, pageSize, cancellationToken));

    private async Task<IActionResult> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Ok(ApiResponse<T>.OK(await operation()));
        }
        catch (FaceCameraException ex)
        {
            // 业务错误消息由服务层构造，不含 token。
            return StatusCode(ex.StatusCode, ApiResponse<T>.Error(ex.Message, ex.Code));
        }
    }

    private FaceCameraActor ResolveActor() => new(
        User.FindFirst("userId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown",
        User.Identity?.Name ?? "unknown");
}

/// <summary>统一给响应加 no-store，包括错误响应。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class FaceCameraNoStoreAttribute : ActionFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Headers.Pragma = "no-cache";
        base.OnResultExecuting(context);
    }
}
