using BlazorApp.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 通知实时通道。连接即按账号加入个人分组，服务端只推不收；客户端不调用任何 Hub 方法。
/// 只接受个人账号会话（含设备账号），服务令牌与浏览器扩展令牌不允许订阅。
/// 后端单实例部署，分组保存在内存中；扩成多实例时需要加 Redis 背板。
/// </summary>
[Authorize]
public sealed class NotificationHub(ILogger<NotificationHub> logger) : Hub
{
    /// <summary>
    /// 放在 /api/ 下：现有各层 Nginx 已把 /api/ 转发到后端，未配 WebSocket 升级头时 SignalR 自动退回长轮询仍可用。
    /// </summary>
    internal const string Path = "/api/hubs/notifications";

    /// <summary>客户端订阅的事件名，载荷为 <see cref="NotificationDto"/>。</summary>
    internal const string NotificationReceivedMethod = "notificationReceived";

    internal static string UserGroup(string userGuid) => $"user:{userGuid.Trim().ToLowerInvariant()}";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        var userGuid = NotificationUserClaims.GetUserGuid(user);
        if (userGuid == null
            || user!.HasClaim(claim => claim.Type == ServiceApiTokenAuthenticationDefaults.TokenTypeClaim)
            || user.HasClaim("token_use", "browser_extension"))
        {
            logger.LogWarning("拒绝通知通道连接：缺少个人账号标识或令牌类型不允许。ConnectionId={ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userGuid));
        await base.OnConnectedAsync();
    }
}

internal sealed class SignalRNotificationRealtimeDispatcher(IHubContext<NotificationHub> hubContext)
    : INotificationRealtimeDispatcher
{
    public Task SendToUsersAsync(
        IReadOnlyCollection<string> userGuids,
        NotificationDto notification,
        CancellationToken cancellationToken = default
    )
    {
        var groups = userGuids
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(NotificationHub.UserGroup)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (groups.Count == 0)
        {
            return Task.CompletedTask;
        }

        return hubContext.Clients
            .Groups(groups)
            .SendAsync(NotificationHub.NotificationReceivedMethod, notification, cancellationToken);
    }
}
