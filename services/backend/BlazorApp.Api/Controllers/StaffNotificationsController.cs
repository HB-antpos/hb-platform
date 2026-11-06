using BlazorApp.Api.Services.StaffNotifications;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers;

/// <summary>
/// 后台「员工通知」：用模板给员工发邮件 / 短信（首个模板「账号已开通」会给员工发放随机密码）。
/// 发送、预检、预览、查记录都要求 <c>Users.ResetPassword</c>：发放密码本质上就是重置密码，沿用现有权限，不新增权限码。
/// 模板的新建 / 修改 / 恢复默认只有管理员能做。
/// 发送接口只负责判断和入队，立即返回批次号；逐人发送在后台任务里完成，前端轮询批次详情拿结果。
/// </summary>
[ApiController]
[Route("api/staff-notifications")]
[Authorize]
public sealed class StaffNotificationsController : ControllerBase
{
    private readonly IStaffNotificationService _service;

    public StaffNotificationsController(IStaffNotificationService service)
    {
        _service = service;
    }

    private string ActorName => User.Identity?.Name ?? "system";

    [HttpGet("templates")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    public async Task<IActionResult> ListTemplates() => Ok(await _service.ListTemplatesAsync());

    [HttpPost("templates")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateTemplate([FromBody] StaffNotificationTemplateSaveDto dto) =>
        Ok(await _service.CreateTemplateAsync(dto ?? new StaffNotificationTemplateSaveDto(), ActorName));

    [HttpPut("templates/{templateKey}")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateTemplate(string templateKey, [FromBody] StaffNotificationTemplateSaveDto dto) =>
        Ok(await _service.UpdateTemplateAsync(templateKey, dto ?? new StaffNotificationTemplateSaveDto(), ActorName));

    [HttpPost("templates/{templateKey}/reset")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ResetTemplate(string templateKey) =>
        Ok(await _service.ResetTemplateAsync(templateKey, ActorName));

    [HttpPost("preflight")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    public async Task<IActionResult> Preflight([FromBody] StaffNotificationPreflightRequestDto dto) =>
        Ok(await _service.PreflightAsync(dto ?? new StaffNotificationPreflightRequestDto()));

    [HttpPost("preview")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    public async Task<IActionResult> Preview([FromBody] StaffNotificationPreviewRequestDto dto) =>
        Ok(await _service.PreviewAsync(dto ?? new StaffNotificationPreviewRequestDto()));

    [HttpPost("send")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    public async Task<IActionResult> Send([FromBody] StaffNotificationSendRequestDto dto) =>
        Ok(await _service.SendAsync(dto ?? new StaffNotificationSendRequestDto(), ActorName));

    [HttpGet("batches")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    public async Task<IActionResult> ListBatches([FromQuery] StaffNotificationBatchQueryDto query) =>
        Ok(await _service.ListBatchesAsync(query ?? new StaffNotificationBatchQueryDto()));

    [HttpGet("batches/{batchId}")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    public async Task<IActionResult> GetBatch(string batchId) => Ok(await _service.GetBatchAsync(batchId));

    [HttpPost("batches/{batchId}/retry")]
    [Authorize(Policy = Permissions.Users.ResetPassword)]
    public async Task<IActionResult> RetryBatch(string batchId) => Ok(await _service.RetryAsync(batchId, ActorName));
}
