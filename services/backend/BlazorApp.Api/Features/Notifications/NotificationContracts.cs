using System.Security.Claims;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 通知接收范围：指定用户与「某门店的成员（可再按权限筛）」取并集。
/// 门店成员按 UserStore 关联计算，只含启用且未删除的账号。
/// </summary>
public sealed record NotificationAudience
{
    public IReadOnlyCollection<string> UserGuids { get; init; } = [];

    public string? StoreCode { get; init; }

    /// <summary>仅对门店成员生效：持有该权限（含超级管理员）才接收。</summary>
    public string? RequiredPermission { get; init; }

    public static NotificationAudience ForUsers(params string[] userGuids) => new() { UserGuids = userGuids };

    public static NotificationAudience ForStore(string storeCode, string? requiredPermission = null) =>
        new() { StoreCode = storeCode, RequiredPermission = requiredPermission };
}

public sealed record NotificationPublishRequest
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required NotificationAudience Audience { get; init; }

    public int Priority { get; init; } = BlazorApp.Shared.Models.AppNotificationPriorities.Normal;

    /// <summary>客户端跳转参数；只放标识，不放敏感内容。</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>通知所属门店（展示与筛选用），与接收范围无关。</summary>
    public string? StoreCode { get; init; }

    /// <summary>业务幂等键，例如 <c>approval:submitted:{id}</c>；同键重复发布不会再发。</summary>
    public string? DedupeKey { get; init; }

    public DateTime? ExpiresAtUtc { get; init; }

    public string CreatedBy { get; init; } = "System";
}

/// <param name="NotificationId">未产生通知（没有接收人）时为空。</param>
/// <param name="RecipientCount">本次新写入的接收人数；去重命中时为 0。</param>
/// <param name="Deduplicated">幂等键已存在，本次没有新建也没有投递。</param>
public sealed record NotificationPublishResult(string? NotificationId, int RecipientCount, bool Deduplicated);

/// <summary>收件箱与实时通道共用的通知视图。</summary>
public sealed class NotificationDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> Data { get; set; } = new Dictionary<string, string>();
    public string? StoreCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}

public sealed class NotificationPageDto
{
    public List<NotificationDto> Items { get; set; } = [];

    /// <summary>下一页游标；为空表示没有更多。</summary>
    public string? NextCursor { get; set; }

    public int UnreadCount { get; set; }
}

public sealed class NotificationMarkReadRequestDto
{
    public List<string> Ids { get; set; } = [];
}

public sealed class NotificationMarkReadResultDto
{
    public int Updated { get; set; }
    public int UnreadCount { get; set; }
}

public interface INotificationPublisher
{
    Task<NotificationPublishResult> PublishAsync(
        NotificationPublishRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>按幂等键撤回：业务已失效（如审批被系统取消）时调用，撤回后不再计入收件箱与未读数。</summary>
    Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default);
}

public interface INotificationRecipientResolver
{
    Task<IReadOnlyList<string>> ResolveAsync(
        NotificationAudience audience,
        CancellationToken cancellationToken = default
    );
}

public interface INotificationInboxService
{
    Task<NotificationPageDto> ListAsync(
        string userGuid,
        string? cursor,
        int take,
        bool unreadOnly,
        CancellationToken cancellationToken = default
    );

    Task<int> GetUnreadCountAsync(string userGuid, CancellationToken cancellationToken = default);

    Task<NotificationMarkReadResultDto> MarkReadAsync(
        string userGuid,
        IReadOnlyCollection<string> notificationIds,
        CancellationToken cancellationToken = default
    );

    Task<NotificationMarkReadResultDto> MarkAllReadAsync(
        string userGuid,
        CancellationToken cancellationToken = default
    );
}

/// <summary>实时投递（SignalR）。尽力而为：失败只记日志，站内消息仍在，客户端重连后按收件箱补齐。</summary>
public interface INotificationRealtimeDispatcher
{
    Task SendToUsersAsync(
        IReadOnlyCollection<string> userGuids,
        NotificationDto notification,
        CancellationToken cancellationToken = default
    );
}

internal static class NotificationUserClaims
{
    /// <summary>与 <see cref="Services.CurrentUserService"/> 同一口径：优先 userId，其次 NameIdentifier。</summary>
    internal static string? GetUserGuid(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst("userId")?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
