using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>当前登录人对某条命中通知的接收情况：通知编号，以及「在收件箱里是否还算未读」。</summary>
internal sealed record FaceHitReadInfo(string NotificationId, bool Unread);

/// <summary>
/// 「当前登录人的命中通知是否未读」的回查，抓拍流（<see cref="FaceCaptureResultService"/>）与命中流（<see cref="FaceHitResultService"/>）共用，
/// 保证两处的 <c>unread</c> / <c>notificationId</c> 与 App 上的红色数字（<c>unread-count?type=face.unwelcome-hit</c>）同一口径：
/// 1. 通知按去重键 <c>unwelcome-hit:{TaskId}</c> 找（与 <see cref="FaceHitNotifyLookup"/>、<see cref="FaceHitNotifier"/> 一致）；
/// 2. 只看<b>本人</b>的接收行：没有接收行（不是收件人、或通知还没发）= 没有通知，<c>unread=false</c>；
/// 3. 「未读」= 本人接收行 ReadAtUtc 为空，<b>并且</b>通知仍可见（未撤回、未过期）——收件箱与未读数都不计已撤回 / 已过期的通知，
///    这里必须一致，否则会出现「红色写 3、点进去是 4」。
/// </summary>
internal static class FaceHitReadLookup
{
    /// <summary>批量取一批任务对应的通知（TaskId → 接收情况）；没有本人接收行的任务不在结果里。</summary>
    public static async Task<Dictionary<string, FaceHitReadInfo>> LoadAsync(
        ISqlSugarClient db,
        string userGuid,
        IReadOnlyCollection<string> taskIds,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FaceHitReadInfo>(StringComparer.Ordinal);
        if (taskIds.Count == 0 || string.IsNullOrWhiteSpace(userGuid))
        {
            return result;
        }

        var prefix = FaceHitNotifier.DedupeKeyPrefix;
        var keys = taskIds.Select(id => prefix + id).ToList();
        var rows = await db.Queryable<AppNotification>()
            .InnerJoin<AppNotificationRecipient>((n, r) => r.NotificationId == n.NotificationId && r.UserGUID == userGuid)
            .Where((n, r) => keys.Contains(n.DedupeKey!))
            .Select((n, r) => new
            {
                n.NotificationId,
                n.DedupeKey,
                n.RevokedAtUtc,
                n.ExpiresAtUtc,
                r.ReadAtUtc,
            })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var taskId = row.DedupeKey![prefix.Length..];
            var visible = row.RevokedAtUtc == null && (row.ExpiresAtUtc == null || row.ExpiresAtUtc > nowUtc);
            result[taskId] = new FaceHitReadInfo(row.NotificationId, visible && row.ReadAtUtc == null);
        }

        return result;
    }
}
