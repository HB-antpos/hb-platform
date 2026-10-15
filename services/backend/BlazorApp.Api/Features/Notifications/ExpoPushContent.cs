using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 一条发给 Expo <c>/send</c> 的消息（字段名按 Expo 协议用 camelCase 序列化）。
/// <c>Title</c> / <c>Body</c> 已按隐私规则处理过（敏感类型为通用文案），<c>Data</c> 同理。
/// </summary>
internal sealed record ExpoPushMessage(
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string> Data,
    [property: JsonPropertyName("sound")] string Sound,
    [property: JsonPropertyName("channelId")] string ChannelId,
    [property: JsonPropertyName("priority")] string Priority,
    [property: JsonPropertyName("ttl")] int Ttl
);

/// <summary>
/// 把一条通知 + 一个令牌变成 Expo 消息的<b>纯函数</b>集合（不碰数据库、网络、时钟），便于单测隐私规则。
/// <para>
/// 隐私：类型命中 <see cref="ExpoPushOptions.SensitiveTypePrefixes"/> 的通知（默认 <c>face.</c>、<c>approval.</c>），
/// 推送 title / body 一律换成通用英文文案，<c>data</c> 只放 <c>notificationId</c> 与 <c>type</c>，不合并通知的 DataJson
/// （里面有 taskId / storeCode / entryId 等标识）。原因：推送会显示在锁屏上，旁人看得到；并且会经过第三方 Expo 服务器。
/// 点开通知后 App 凭登录态从收件箱接口拉取完整内容。
/// </para>
/// </summary>
internal static class ExpoPushContent
{
    /// <summary>敏感类型推送的通用标题。</summary>
    internal const string GenericTitle = "HB";

    /// <summary>敏感类型推送的通用正文。</summary>
    internal const string GenericBody = "You have a new notification. Open the app to view it.";

    /// <summary><c>data</c> 的 UTF-8 序列化大小上限（Expo 单条消息总上限 4096 字节）。</summary>
    internal const int MaxDataBytes = 4096;

    /// <summary>消息存活时间上限：24 小时（秒）。</summary>
    internal const int MaxTtlSeconds = 24 * 60 * 60;

    internal const string DataKeyNotificationId = "notificationId";
    internal const string DataKeyType = "type";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // 令牌与文案里可能有非 ASCII 字符；保持 UTF-8 原样输出，不转义成 \uXXXX（既省字节也便于排障对照）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>由通知与目标令牌构造消息。<paramref name="nowUtc"/> 只用于计算 ttl。</summary>
    internal static ExpoPushMessage Build(AppNotification notification, string token, ExpoPushOptions options, DateTime nowUtc)
    {
        var sensitive = options.IsSensitiveType(notification.Type);
        var (title, body) = ResolveText(sensitive, notification.Title, notification.Body);
        return new ExpoPushMessage(
            To: token,
            Title: title,
            Body: body,
            Data: BuildData(notification, sensitive),
            Sound: "default",
            ChannelId: "default",
            Priority: ResolvePriority(notification.Priority),
            Ttl: ComputeTtlSeconds(notification.ExpiresAtUtc, nowUtc)
        );
    }

    /// <summary>敏感类型 → 通用文案；非敏感类型 → 通知原本的 title / body。</summary>
    internal static (string Title, string Body) ResolveText(bool sensitive, string title, string body) =>
        sensitive ? (GenericTitle, GenericBody) : (title, body);

    /// <summary>通知优先级 High → <c>high</c>，其余 → <c>default</c>。</summary>
    internal static string ResolvePriority(int notificationPriority) =>
        notificationPriority == AppNotificationPriorities.High ? "high" : "default";

    /// <summary>
    /// 剩余存活秒数：有 <paramref name="expiresAtUtc"/> 则取剩余秒数（至少 1，上限 24 小时），否则 24 小时。
    /// 已过期的通知发送前就会被跳过，这里的 1 秒下限只是兜底，避免传出 0（Expo 把 0 理解成「立即投递否则丢弃」）。
    /// </summary>
    internal static int ComputeTtlSeconds(DateTime? expiresAtUtc, DateTime nowUtc)
    {
        if (expiresAtUtc is null)
        {
            return MaxTtlSeconds;
        }

        var remaining = (long)Math.Floor((expiresAtUtc.Value - nowUtc).TotalSeconds);
        return (int)Math.Clamp(remaining, 1, MaxTtlSeconds);
    }

    /// <summary>
    /// <c>data</c>：恒有 <c>notificationId</c> 与 <c>type</c>；非敏感类型再合并通知的 DataJson（键冲突时以这两个固定键为准）。
    /// 合并后序列化超过 <see cref="MaxDataBytes"/> 字节，或 DataJson 不是合法的「字符串键值对象」，只保留固定的两项。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildData(AppNotification notification, bool sensitive)
    {
        var minimal = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DataKeyNotificationId] = notification.NotificationId,
            [DataKeyType] = notification.Type,
        };
        if (sensitive || string.IsNullOrWhiteSpace(notification.DataJson))
        {
            return minimal;
        }

        Dictionary<string, string>? extra;
        try
        {
            extra = JsonSerializer.Deserialize<Dictionary<string, string>>(notification.DataJson);
        }
        catch (JsonException)
        {
            // 跳转参数只影响点击后去哪，坏数据不应让整条推送发不出去。
            return minimal;
        }

        if (extra is null || extra.Count == 0)
        {
            return minimal;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in extra)
        {
            if (key is DataKeyNotificationId or DataKeyType || value is null)
            {
                continue;
            }

            merged[key] = value;
        }

        merged[DataKeyNotificationId] = notification.NotificationId;
        merged[DataKeyType] = notification.Type;

        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(merged, SerializerOptions)) <= MaxDataBytes
            ? merged
            : minimal;
    }

    /// <summary>序列化为 /send 请求体（JSON 数组，顺序与 <paramref name="messages"/> 一一对应，Expo 返回的 tickets 也按此顺序）。</summary>
    internal static string SerializeSendBody(IReadOnlyList<ExpoPushMessage> messages) =>
        JsonSerializer.Serialize(messages, SerializerOptions);

    /// <summary>序列化为 /getReceipts 请求体：<c>{"ids":[...]}</c>。</summary>
    internal static string SerializeReceiptsBody(IReadOnlyList<string> ticketIds) =>
        JsonSerializer.Serialize(new { ids = ticketIds }, SerializerOptions);
}

/// <summary>失败重试的退避时间表（纯函数，抖动由调用方传入，便于单测）。</summary>
internal static class ExpoPushBackoff
{
    /// <summary>第 n 次失败后等待的基础时间（n 从 1 开始）：5 秒、30 秒、2 分钟、10 分钟、1 小时；超过表长按最后一项。</summary>
    internal static readonly IReadOnlyList<TimeSpan> Schedule =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    /// <summary>抖动幅度：基础时间的 ±10%。</summary>
    internal const double JitterRatio = 0.10;

    /// <param name="failureCount">已累计的失败次数（≥ 1）。</param>
    /// <param name="jitterUnit">[0, 1) 的随机数；0.5 表示不抖动，0 取最小（-10%），接近 1 取最大（+10%）。</param>
    internal static TimeSpan Delay(int failureCount, double jitterUnit)
    {
        var index = Math.Clamp(failureCount, 1, Schedule.Count) - 1;
        var factor = 1 + ((Math.Clamp(jitterUnit, 0, 1) * 2) - 1) * JitterRatio;
        return TimeSpan.FromTicks((long)(Schedule[index].Ticks * factor));
    }
}
