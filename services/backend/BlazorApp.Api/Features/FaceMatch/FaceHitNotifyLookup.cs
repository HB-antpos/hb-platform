using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>某条命中任务通知的发送情况（只有计数，没有收件人姓名）。</summary>
internal sealed record FaceHitNotifyInfo(string State, DateTime? NotifiedAtUtc, int RecipientCount, int ReadCount);

/// <summary>
/// 命中通知发送情况的回查：通知平台里每条命中对应一条 AppNotification（DedupeKey = unwelcome-hit:{TaskId}）与若干接收人行。
/// 复核任务列表与「抓拍与比对结果」页都用它，保证两处口径一致。
/// </summary>
internal static class FaceHitNotifyLookup
{
    /// <summary>
    /// 批量取一批任务的通知情况，返回 TaskId → 情况。
    /// 状态：有通知行 = sent；没有通知行但任务已标记处理 = skipped（发布前已被复核或图片已到期）；否则 pending。
    /// </summary>
    public static async Task<Dictionary<string, FaceHitNotifyInfo>> LoadAsync(
        ISqlSugarClient db, IReadOnlyCollection<FaceMatchTask> tasks, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FaceHitNotifyInfo>(StringComparer.Ordinal);
        if (tasks.Count == 0)
        {
            return result;
        }

        var keyToTask = tasks.ToDictionary(t => FaceHitNotifier.DedupeKeyPrefix + t.TaskId, t => t, StringComparer.Ordinal);
        var keys = keyToTask.Keys.ToList();
        var notifications = await db.Queryable<AppNotification>()
            .Where(n => keys.Contains(n.DedupeKey!))
            .Select(n => new { n.NotificationId, n.DedupeKey, n.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        var notificationIds = notifications.Select(n => n.NotificationId).ToList();
        var recipients = notificationIds.Count == 0
            ? []
            : await db.Queryable<AppNotificationRecipient>()
                .Where(r => notificationIds.Contains(r.NotificationId))
                .Select(r => new { r.NotificationId, r.ReadAtUtc })
                .ToListAsync(cancellationToken);
        var stats = recipients
            .GroupBy(r => r.NotificationId)
            .ToDictionary(g => g.Key, g => (Total: g.Count(), Read: g.Count(r => r.ReadAtUtc != null)));
        var byKey = notifications.ToDictionary(n => n.DedupeKey!, n => n, StringComparer.Ordinal);

        foreach (var (key, task) in keyToTask)
        {
            if (byKey.TryGetValue(key, out var notification))
            {
                var (total, read) = stats.GetValueOrDefault(notification.NotificationId);
                result[task.TaskId] = new FaceHitNotifyInfo(
                    FaceHitNotifyStates.Sent, FaceMatchRules.AsUtc(notification.CreatedAtUtc), total, read);
            }
            else
            {
                result[task.TaskId] = new FaceHitNotifyInfo(
                    task.NotifiedAtUtc != null ? FaceHitNotifyStates.Skipped : FaceHitNotifyStates.Pending,
                    FaceMatchRules.AsUtc(task.NotifiedAtUtc),
                    0,
                    0);
            }
        }

        return result;
    }
}
