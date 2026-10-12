using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 通知平台的唯一发布入口：先把通知与接收人落库（站内消息以库为准），提交后再走实时通道。
/// 实时投递失败不影响发布结果，客户端重连后按收件箱补齐；后续的 App 推送同样挂在提交之后。
/// </summary>
internal sealed class NotificationPublisher(
    SqlSugarContext context,
    INotificationRecipientResolver recipientResolver,
    INotificationRealtimeDispatcher realtimeDispatcher,
    TimeProvider timeProvider,
    ILogger<NotificationPublisher> logger
) : INotificationPublisher
{
    internal const int MaxTypeLength = 64;
    internal const int MaxTitleLength = 200;
    internal const int MaxBodyLength = 1000;
    internal const int MaxStoreCodeLength = 50;
    internal const int MaxDedupeKeyLength = 200;
    internal const int MaxCreatedByLength = 100;
    internal const int MaxDataEntries = 20;
    internal const int MaxDataJsonLength = 4000;

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<NotificationPublishResult> PublishAsync(
        NotificationPublishRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var normalized = Normalize(request);

        if (normalized.DedupeKey != null)
        {
            var existingId = await FindIdByDedupeKeyAsync(normalized.DedupeKey, cancellationToken);
            if (existingId != null)
            {
                return new NotificationPublishResult(existingId, 0, Deduplicated: true);
            }
        }

        var recipients = await recipientResolver.ResolveAsync(request.Audience, cancellationToken);
        if (recipients.Count == 0)
        {
            logger.LogInformation(
                "通知没有接收人，未发布: Type={Type}, StoreCode={StoreCode}, DedupeKey={DedupeKey}",
                normalized.Type,
                normalized.StoreCode,
                normalized.DedupeKey
            );
            return new NotificationPublishResult(null, 0, Deduplicated: false);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var notification = new AppNotification
        {
            NotificationId = Guid.NewGuid().ToString("N"),
            Type = normalized.Type,
            Priority = normalized.Priority,
            Title = normalized.Title,
            Body = normalized.Body,
            DataJson = normalized.DataJson,
            StoreCode = normalized.StoreCode,
            DedupeKey = normalized.DedupeKey,
            CreatedAtUtc = now,
            ExpiresAtUtc = normalized.ExpiresAtUtc,
            CreatedBy = normalized.CreatedBy,
        };
        var rows = recipients
            .Select(userGuid => new AppNotificationRecipient
            {
                NotificationId = notification.NotificationId,
                UserGUID = userGuid,
                CreatedAtUtc = now,
            })
            .ToList();

        var transaction = await _db.Ado.UseTranAsync(async () =>
        {
            await _db.Insertable(notification).ExecuteCommandAsync(cancellationToken);
            await _db.Insertable(rows).ExecuteCommandAsync(cancellationToken);
        });
        if (!transaction.IsSuccess)
        {
            var error = transaction.ErrorException ?? new InvalidOperationException("保存通知失败");
            // 并发下同一幂等键被别的请求抢先写入：以先写入的那条为准，本次不再投递。
            if (normalized.DedupeKey != null && IsUniqueViolation(error))
            {
                var winnerId = await FindIdByDedupeKeyAsync(normalized.DedupeKey, cancellationToken);
                if (winnerId != null)
                {
                    return new NotificationPublishResult(winnerId, 0, Deduplicated: true);
                }
            }

            throw error;
        }

        await DispatchRealtimeAsync(recipients, NotificationMapper.ToDto(notification, readAtUtc: null));
        return new NotificationPublishResult(notification.NotificationId, rows.Count, Deduplicated: false);
    }

    public async Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default)
    {
        var key = NormalizeOptional(dedupeKey, MaxDedupeKeyLength, nameof(dedupeKey));
        if (key == null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await _db.Updateable<AppNotification>()
            .SetColumns(notification => notification.RevokedAtUtc == now)
            .Where(notification => notification.DedupeKey == key && notification.RevokedAtUtc == null)
            .ExecuteCommandAsync(cancellationToken);
        return updated > 0;
    }

    private async Task DispatchRealtimeAsync(IReadOnlyCollection<string> recipients, NotificationDto dto)
    {
        try
        {
            // 通知已提交，调用方取消也不应让已落库的通知漏掉实时投递。
            await realtimeDispatcher.SendToUsersAsync(recipients, dto, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "通知实时投递失败，客户端将按收件箱补齐: NotificationId={NotificationId}, Type={Type}",
                dto.Id,
                dto.Type
            );
        }
    }

    private async Task<string?> FindIdByDedupeKeyAsync(string dedupeKey, CancellationToken cancellationToken)
    {
        return await _db.Queryable<AppNotification>()
            .Where(notification => notification.DedupeKey == dedupeKey)
            .Select(notification => notification.NotificationId)
            .FirstAsync(cancellationToken);
    }

    private static NormalizedRequest Normalize(NotificationPublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Audience);

        if (!AppNotificationPriorities.IsValid(request.Priority))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Priority, "通知优先级无效。");
        }

        return new NormalizedRequest(
            Type: NormalizeRequired(request.Type, MaxTypeLength, nameof(request.Type)),
            Title: NormalizeRequired(request.Title, MaxTitleLength, nameof(request.Title)),
            Body: NormalizeRequired(request.Body, MaxBodyLength, nameof(request.Body)),
            Priority: request.Priority,
            DataJson: SerializeData(request.Data),
            StoreCode: NormalizeOptional(request.StoreCode, MaxStoreCodeLength, nameof(request.StoreCode)),
            DedupeKey: NormalizeOptional(request.DedupeKey, MaxDedupeKeyLength, nameof(request.DedupeKey)),
            ExpiresAtUtc: request.ExpiresAtUtc,
            CreatedBy: NormalizeOptional(request.CreatedBy, MaxCreatedByLength, nameof(request.CreatedBy)) ?? "System"
        );
    }

    private static string NormalizeRequired(string? value, int maxLength, string name)
    {
        return NormalizeOptional(value, maxLength, name)
            ?? throw new ArgumentException($"通知字段 {name} 不能为空。", name);
    }

    private static string? NormalizeOptional(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"通知字段 {name} 超过 {maxLength} 个字符。", name);
        }

        return trimmed;
    }

    private static string? SerializeData(IReadOnlyDictionary<string, string>? data)
    {
        if (data == null || data.Count == 0)
        {
            return null;
        }

        if (data.Count > MaxDataEntries)
        {
            throw new ArgumentException($"通知跳转参数不能超过 {MaxDataEntries} 项。", nameof(data));
        }

        var json = JsonSerializer.Serialize(data);
        if (json.Length > MaxDataJsonLength)
        {
            throw new ArgumentException($"通知跳转参数超过 {MaxDataJsonLength} 个字符。", nameof(data));
        }

        return json;
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            {
                return true;
            }

            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record NormalizedRequest(
        string Type,
        string Title,
        string Body,
        int Priority,
        string? DataJson,
        string? StoreCode,
        string? DedupeKey,
        DateTime? ExpiresAtUtc,
        string CreatedBy
    );
}

internal static class NotificationMapper
{
    private static readonly IReadOnlyDictionary<string, string> EmptyData = new Dictionary<string, string>();

    internal static NotificationDto ToDto(AppNotification notification, DateTime? readAtUtc) => new()
    {
        Id = notification.NotificationId,
        Type = notification.Type,
        Priority = notification.Priority,
        Title = notification.Title,
        Body = notification.Body,
        Data = DeserializeData(notification.DataJson),
        StoreCode = notification.StoreCode,
        CreatedAtUtc = DateTime.SpecifyKind(notification.CreatedAtUtc, DateTimeKind.Utc),
        ReadAtUtc = readAtUtc.HasValue ? DateTime.SpecifyKind(readAtUtc.Value, DateTimeKind.Utc) : null,
    };

    private static IReadOnlyDictionary<string, string> DeserializeData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyData;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? EmptyData;
        }
        catch (JsonException)
        {
            // 跳转参数只影响点击后去哪，坏数据不应让整页收件箱打不开。
            return EmptyData;
        }
    }
}
