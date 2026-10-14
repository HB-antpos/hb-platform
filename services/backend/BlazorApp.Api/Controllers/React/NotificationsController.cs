using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 站内消息收件箱：所有端（App、收银端、网页后台）共用，只能读写本人的通知。
/// 实时到达走 <see cref="NotificationHub"/>；客户端重连或回到前台时调本接口补齐。
/// </summary>
[ApiController]
[Route("api/react/v1/notifications")]
[Authorize]
public sealed class NotificationsController(
    INotificationInboxService inboxService,
    INotificationPublisher publisher,
    ILogger<NotificationsController> logger
) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List(
        [FromQuery] string? cursor,
        [FromQuery] int take = NotificationInboxService.DefaultPageSize,
        [FromQuery] bool unreadOnly = false,
        CancellationToken cancellationToken = default
    ) =>
        RunAsync<NotificationPageDto>(nameof(List), userGuid =>
            inboxService.ListAsync(userGuid, cursor, take, unreadOnly, cancellationToken), cancellationToken);

    /// <summary>未读数；可选 <c>type</c> 只统计某个业务类型（如 face.unwelcome-hit），格式不合法返回 400。</summary>
    [HttpGet("unread-count")]
    public Task<IActionResult> UnreadCount([FromQuery] string? type, CancellationToken cancellationToken) =>
        RunAsync<int>(nameof(UnreadCount), userGuid =>
            inboxService.GetUnreadCountAsync(userGuid, type, cancellationToken), cancellationToken);

    [HttpPost("read")]
    public Task<IActionResult> MarkRead(
        [FromBody] NotificationMarkReadRequestDto? request,
        CancellationToken cancellationToken
    ) =>
        RunAsync<NotificationMarkReadResultDto>(nameof(MarkRead), userGuid =>
            inboxService.MarkReadAsync(userGuid, request?.Ids ?? [], cancellationToken), cancellationToken);

    /// <summary>全部标为已读；可选 <c>type</c> 只标该类型，并返回该类型的剩余未读数（不带 type 时返回总未读数）。</summary>
    [HttpPost("read-all")]
    public Task<IActionResult> MarkAllRead([FromQuery] string? type, CancellationToken cancellationToken) =>
        RunAsync<NotificationMarkReadResultDto>(nameof(MarkAllRead), userGuid =>
            inboxService.MarkAllReadAsync(userGuid, type, cancellationToken), cancellationToken);

    /// <summary>给自己发一条测试通知，用于验证 App、收银端的实时通道与收件箱链路。</summary>
    [HttpPost("test")]
    public Task<IActionResult> SendTest([FromQuery] bool high = false, CancellationToken cancellationToken = default) =>
        RunAsync<NotificationPublishResult>(nameof(SendTest), userGuid =>
            publisher.PublishAsync(new NotificationPublishRequest
            {
                Type = AppNotificationTypes.Test,
                Title = "测试通知",
                Body = $"这是一条测试通知（{DateTime.UtcNow:HH:mm:ss} UTC），收到说明通知链路正常。",
                Priority = high ? AppNotificationPriorities.High : AppNotificationPriorities.Normal,
                Audience = NotificationAudience.ForUsers(userGuid),
                CreatedBy = User.Identity?.Name ?? userGuid,
            }, cancellationToken), cancellationToken);

    private async Task<IActionResult> RunAsync<T>(
        string action,
        Func<string, Task<T>> operation,
        CancellationToken cancellationToken
    )
    {
        var userGuid = NotificationUserClaims.GetUserGuid(User);
        if (userGuid == null)
        {
            return Unauthorized(ApiResponse<T>.Error("缺少用户标识", "UNAUTHORIZED"));
        }

        try
        {
            return Ok(ApiResponse<T>.OK(await operation(userGuid)));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<T>.Error(ex.Message, "BAD_REQUEST"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "通知接口失败: Action={Action}, User={UserGuid}", action, userGuid);
            return StatusCode(500, ApiResponse<T>.Error("服务器内部错误"));
        }
    }
}
