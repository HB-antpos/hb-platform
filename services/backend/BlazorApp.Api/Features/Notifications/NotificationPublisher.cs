using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 通知平台的唯一发布入口：先把通知与接收人落库（站内消息以库为准），提交后再走实时通道。
/// 实时投递失败不影响发布结果，客户端重连后按收件箱补齐。
/// <para>
/// App 系统推送（走 Expo 推送服务）采用发件箱：开启 <c>Push:Expo:Enabled</c> 时，把「通知 × 接收人的活动推送设备」
/// 作为 <see cref="AppPushDelivery"/> 行与通知、接收人<b>在同一个事务里</b>写入，由 <see cref="ExpoPushBackgroundService"/> 之后发送并按需重试，
/// 进程重启不丢。关闭（默认）时发布器不查设备表、不写发件箱，行为与没有推送功能时完全一致。
/// </para>
/// </summary>
internal sealed class NotificationPublisher(
    SqlSugarContext context,
    INotificationRecipientResolver recipientResolver,
    INotificationRealtimeDispatcher realtimeDispatcher,
    TimeProvider timeProvider,
    ILogger<NotificationPublisher> logger,
    IOptionsMonitor<ExpoPushOptions>? pushOptions = null
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
        // 发布器自己用 UseTranAsync 落库，而 SqlSugar 不支持事务嵌套：内层一提交，外层已写的业务行就被一并提交，
        // 外层随后的回滚或失败都不再生效，内层失败还会把外层的业务行悄悄回滚（SQL Server 实测，见 PR 说明）。
        // 因此必须在业务事务提交之后再发布；这里把误用变成立刻可见的异常，而不是静默的数据错乱。
        if (_db.Ado.IsAnyTran())
        {
            throw new InvalidOperationException(
                "通知必须在业务事务提交之后发布：当前连接上已有未结束的事务。"
            );
        }

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

        // 推送发件箱行在事务之外先算好（设备表查询失败不能污染事务），再随通知一起在同一个事务里落库。
        var pushDeliveries = await BuildPushDeliveriesAsync(notification, recipients, now, cancellationToken);

        var transaction = await _db.Ado.UseTranAsync(async () =>
        {
            await _db.Insertable(notification).ExecuteCommandAsync(cancellationToken);
            await _db.Insertable(rows).ExecuteCommandAsync(cancellationToken);
            if (pushDeliveries.Count > 0)
            {
                await _db.Insertable(pushDeliveries).ExecuteCommandAsync(cancellationToken);
            }
        });
        if (!transaction.IsSuccess)
        {
            var error = transaction.ErrorException ?? new InvalidOperationException("保存通知失败");
            // 并发下同一幂等键被别的请求抢先写入：以先写入的那条为准，本次不再投递。
            if (normalized.DedupeKey != null && SqlErrorClassifier.IsUniqueViolation(error))
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

    /// <summary>
    /// 为这批接收人的每个活动推送设备生成一行发件箱记录（State=Pending，立即可发）。
    /// 不写发件箱（返回空）的情况：推送未启用、通知已过期、接收人没有活动设备、设备查询失败。
    /// 设备查询失败（典型是开关打开但迁移没跑、表不存在）只记一条 Warning，通知照常发布——不能因为推送拖垮所有通知。
    /// </summary>
    private async Task<List<AppPushDelivery>> BuildPushDeliveriesAsync(
        AppNotification notification,
        IReadOnlyCollection<string> recipients,
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        if (pushOptions?.CurrentValue is not { Enabled: true })
        {
            return [];
        }

        if (notification.ExpiresAtUtc != null && notification.ExpiresAtUtc <= now)
        {
            return [];
        }

        try
        {
            var devices = new List<AppPushDevice>();
            // IN 列表分块，避免大范围广播时超过参数上限。
            foreach (var chunk in recipients.Chunk(500))
            {
                var part = await _db.Queryable<AppPushDevice>()
                    .Where(device => chunk.Contains(device.UserGUID) && device.RevokedAtUtc == null)
                    .ToListAsync(cancellationToken);
                devices.AddRange(part);
            }

            return devices
                .DistinctBy(device => device.PushDeviceId, StringComparer.Ordinal)
                .Select(device => new AppPushDelivery
                {
                    DeliveryId = Guid.NewGuid().ToString("N"),
                    NotificationId = notification.NotificationId,
                    PushDeviceId = device.PushDeviceId,
                    UserGUID = device.UserGUID,
                    State = AppPushDeliveryStates.Pending,
                    Attempts = 0,
                    NextAttemptAtUtc = now,
                    CreatedAtUtc = now,
                })
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 只记异常类型与是否「表不存在」，不传异常对象：SqlSugar 的异常消息会带 SQL 与参数。
            logger.LogWarning(
                "App 推送设备查询失败，通知照常发布但不写推送发件箱: Type={Type}, Error={ErrorType}, SchemaMissing={SchemaMissing}",
                notification.Type,
                ex.GetType().Name,
                SqlErrorClassifier.IsMissingObject(ex)
            );
            return [];
        }
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
