using System.Globalization;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 站内消息收件箱：只读本人的接收记录；已撤回、已过期的通知不出现在列表与未读数里。
/// 翻页用（创建时间, 通知 Id）游标，新通知不断写入时不会重复或跳过。
/// </summary>
internal sealed class NotificationInboxService(SqlSugarContext context, TimeProvider timeProvider)
    : INotificationInboxService
{
    internal const int DefaultPageSize = 20;
    internal const int MaxPageSize = 50;
    internal const int MaxMarkReadIds = 200;

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<NotificationPageDto> ListAsync(
        string userGuid,
        string? cursor,
        int take,
        bool unreadOnly,
        CancellationToken cancellationToken = default
    )
    {
        var pageSize = take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var query = VisibleQuery(userGuid, now);
        if (unreadOnly)
        {
            query = query.Where((recipient, notification) => recipient.ReadAtUtc == null);
        }

        if (TryDecodeCursor(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where((recipient, notification) =>
                recipient.CreatedAtUtc < cursorCreatedAt
                || (recipient.CreatedAtUtc == cursorCreatedAt
                    && SqlFunc.LessThan(recipient.NotificationId, cursorId)));
        }

        var rows = await query
            .OrderBy((recipient, notification) => recipient.CreatedAtUtc, OrderByType.Desc)
            .OrderBy((recipient, notification) => recipient.NotificationId, OrderByType.Desc)
            .Select((recipient, notification) => new InboxRow
            {
                NotificationId = notification.NotificationId,
                Type = notification.Type,
                Priority = notification.Priority,
                Title = notification.Title,
                Body = notification.Body,
                DataJson = notification.DataJson,
                StoreCode = notification.StoreCode,
                CreatedAtUtc = recipient.CreatedAtUtc,
                ReadAtUtc = recipient.ReadAtUtc,
            })
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var page = rows.Take(pageSize).ToList();
        return new NotificationPageDto
        {
            Items = page.Select(row => NotificationMapper.ToDto(row.ToEntity(), row.ReadAtUtc)).ToList(),
            NextCursor = hasMore ? EncodeCursor(page[^1].ToEntity()) : null,
            UnreadCount = await GetUnreadCountAsync(userGuid, cancellationToken),
        };
    }

    public async Task<int> GetUnreadCountAsync(string userGuid, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await VisibleQuery(userGuid, now)
            .Where((recipient, notification) => recipient.ReadAtUtc == null)
            .CountAsync(cancellationToken);
    }

    public async Task<NotificationMarkReadResultDto> MarkReadAsync(
        string userGuid,
        IReadOnlyCollection<string> notificationIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = (notificationIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count > MaxMarkReadIds)
        {
            throw new ArgumentException($"一次最多标记 {MaxMarkReadIds} 条通知。", nameof(notificationIds));
        }

        var updated = 0;
        if (ids.Count > 0)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            // 只更新本人的未读记录：别人的通知 Id 传进来也改不到，重复标记不会刷新已读时间。
            updated = await _db.Updateable<AppNotificationRecipient>()
                .SetColumns(recipient => recipient.ReadAtUtc == now)
                .Where(recipient =>
                    recipient.UserGUID == userGuid
                    && ids.Contains(recipient.NotificationId)
                    && recipient.ReadAtUtc == null)
                .ExecuteCommandAsync(cancellationToken);
        }

        return new NotificationMarkReadResultDto
        {
            Updated = updated,
            UnreadCount = await GetUnreadCountAsync(userGuid, cancellationToken),
        };
    }

    public async Task<NotificationMarkReadResultDto> MarkAllReadAsync(
        string userGuid,
        CancellationToken cancellationToken = default
    )
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await _db.Updateable<AppNotificationRecipient>()
            .SetColumns(recipient => recipient.ReadAtUtc == now)
            .Where(recipient => recipient.UserGUID == userGuid && recipient.ReadAtUtc == null)
            .ExecuteCommandAsync(cancellationToken);

        return new NotificationMarkReadResultDto
        {
            Updated = updated,
            UnreadCount = await GetUnreadCountAsync(userGuid, cancellationToken),
        };
    }

    private ISugarQueryable<AppNotificationRecipient, AppNotification> VisibleQuery(string userGuid, DateTime now)
    {
        return _db.Queryable<AppNotificationRecipient>()
            .InnerJoin<AppNotification>((recipient, notification) =>
                recipient.NotificationId == notification.NotificationId)
            .Where((recipient, notification) =>
                recipient.UserGUID == userGuid
                && notification.RevokedAtUtc == null
                && (notification.ExpiresAtUtc == null || notification.ExpiresAtUtc > now));
    }

    internal static string EncodeCursor(AppNotification notification)
    {
        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{notification.CreatedAtUtc.Ticks}:{notification.NotificationId}"
        );
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryDecodeCursor(string? cursor, out DateTime createdAtUtc, out string notificationId)
    {
        createdAtUtc = default;
        notificationId = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 200)
        {
            return false;
        }

        try
        {
            var base64 = cursor.Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var separator = raw.IndexOf(':');
            if (separator <= 0
                || !long.TryParse(raw[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks
                || ticks > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            var id = raw[(separator + 1)..];
            if (id.Length == 0 || id.Length > 32)
            {
                return false;
            }

            createdAtUtc = new DateTime(ticks, DateTimeKind.Utc);
            notificationId = id;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class InboxRow
    {
        public string NotificationId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Priority { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? DataJson { get; set; }
        public string? StoreCode { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ReadAtUtc { get; set; }

        public AppNotification ToEntity() => new()
        {
            NotificationId = NotificationId,
            Type = Type,
            Priority = Priority,
            Title = Title,
            Body = Body,
            DataJson = DataJson,
            StoreCode = StoreCode,
            CreatedAtUtc = CreatedAtUtc,
        };
    }
}
