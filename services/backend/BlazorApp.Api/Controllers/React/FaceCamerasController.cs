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
/// 查看抓拍图片是独立权限 <see cref="Permissions.FaceCapture.Images.View"/>：管理摄像机不自动包含它，
/// 抓拍列表只在同时持有该权限时才返回图片元数据，图片内容接口由服务端转发字节（不暴露 COS 地址）。
/// </summary>
[ApiController]
[Route("api/react/v1/face-cameras")]
[Authorize(Policy = Permissions.FaceCapture.Cameras.Manage)]
[FaceCameraNoStore]
public sealed class FaceCamerasController(
    IFaceCameraAdminService service,
    IFaceCaptureImageService imageService,
    IAuthorizationService authorizationService) : ControllerBase
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

    /// <summary>
    /// 最近抓拍记录（元数据）。图片元数据（不含对象键和地址）只在调用者同时持有 FaceCapture.Images.View 时返回，
    /// 否则 images 为空数组且 canViewImages=false。
    /// </summary>
    [HttpGet("{cameraId}/captures")]
    public async Task<IActionResult> Captures(
        string cameraId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = FaceCameraAdminService.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var canViewImages = await HasImagesViewPermissionAsync();
        return await RunAsync(() => service.ListCapturesAsync(cameraId, page, pageSize, canViewImages, cancellationToken));
    }

    /// <summary>
    /// 图片内容（服务端转发字节）。对象键只来自数据库，客户端只传三个编号；归属不一致一律 404。
    /// 权限在动作内判定而不是用 [Authorize]：无权限的尝试也要进入审计日志。
    /// 响应只有图片字节：Content-Type 只会是 image/jpeg 或 image/png，不缓存、不嗅探、不带 ETag 与任何 COS 响应头。
    /// </summary>
    [HttpGet("{cameraId}/captures/{captureId}/images/{imageId}/content")]
    public async Task<IActionResult> ImageContent(
        string cameraId, string captureId, string imageId, CancellationToken cancellationToken)
    {
        try
        {
            var content = await imageService.GetContentAsync(
                cameraId, captureId, imageId, ResolveActor(), await HasImagesViewPermissionAsync(), cancellationToken);
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers.ContentDisposition = "inline";
            return File(content.Bytes, content.ContentType);
        }
        catch (FaceCameraException ex)
        {
            // 错误体是统一信封（JSON），消息由服务层固定构造，不含桶名、密钥、签名、对象键或地址。
            return StatusCode(ex.StatusCode, ApiResponse<object>.Error(ex.Message, ex.Code));
        }
    }

    private async Task<bool> HasImagesViewPermissionAsync() =>
        (await authorizationService.AuthorizeAsync(User, null, Permissions.FaceCapture.Images.View)).Succeeded;

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
