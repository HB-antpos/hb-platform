namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 配置节 <c>Push:Expo</c>（环境变量 <c>Push__Expo__*</c>）：App 系统推送，走 Expo 推送服务
/// （<c>POST {Endpoint}/send</c> 与 <c>POST {Endpoint}/getReceipts</c>），后端不直连 APNs / FCM，也不碰 .p8 与 FCM 私钥（它们登记在 EAS）。
/// <para>
/// <b>默认关闭</b>：<see cref="Enabled"/> = false 时，通知发布器不写推送发件箱、后台任务不发任何请求，
/// 行为与没有这个功能时完全一致（令牌注册接口仍然可用，App 可以先上报令牌）。
/// </para>
/// <para>
/// 日志规则：<see cref="AccessToken"/> 是凭据，永不打印；本类型覆盖了 <see cref="ToString"/>，避免被日志解构带出。
/// </para>
/// </summary>
public sealed class ExpoPushOptions
{
    public const string SectionName = "Push:Expo";

    /// <summary>Expo 单次 /send 的消息数上限（官方文档规定 100 条）。</summary>
    public const int ExpoMaxMessagesPerRequest = 100;

    /// <summary>Expo 单次 /getReceipts 的 ticket 数上限（官方文档规定 1000 个）。</summary>
    public const int ExpoMaxReceiptIdsPerRequest = 1000;

    /// <summary>总开关；默认 false。</summary>
    public bool Enabled { get; set; }

    /// <summary>Expo 推送服务地址（不含 /send、/getReceipts）。必须是 https（本机回环地址除外，供联调用）。</summary>
    public string Endpoint { get; set; } = "https://exp.host/--/api/v2/push";

    /// <summary>
    /// Expo Access Token（EAS 账号的 Robot / Personal Access Token），可空；配置后每个请求带 <c>Authorization: Bearer</c>。
    /// 只从环境变量 <c>EXPO_ACCESS_TOKEN</c>（compose 映射到 <c>Push__Expo__AccessToken</c>）注入，不写进仓库，日志绝不打印。
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>单条推送最多发送次数（含首次）；失败达到该次数标记为 Failed。小于 1 视为 1。</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>每次 /send 请求的消息数；Expo 上限 100，超过按 100 处理，小于 1 视为 1。</summary>
    public int BatchSize { get; set; } = ExpoMaxMessagesPerRequest;

    /// <summary>发送成功（拿到 ticket）后等多少分钟再取回执；Expo 回执一般在发送约 15 分钟后可查。小于 1 视为 1。</summary>
    public int ReceiptDelayMinutes { get; set; } = 15;

    /// <summary>已结束（Delivered / Failed / Skipped）的发件箱记录保留天数，超过后由后台任务清理。小于 1 视为 1。</summary>
    public int DeliveryRetentionDays { get; set; } = 14;

    /// <summary>后台任务轮询间隔（秒）。小于 1 视为 1。</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>默认的敏感通知类型前缀：人脸比对（face.*）与考勤审批（approval.*）。</summary>
    public static readonly IReadOnlyList<string> DefaultSensitiveTypePrefixes = ["face.", "approval."];

    /// <summary>
    /// 敏感通知类型前缀：类型以其中任一项开头的通知，推送只发通用文案（不带门店名、分数、姓名、日期、编号），
    /// <c>data</c> 里也不合并通知的 DataJson。原因见运行手册「隐私」一节：锁屏上别人看得到，推送还会经过第三方 Expo 服务器。
    /// 未配置（null）时用 <see cref="DefaultSensitiveTypePrefixes"/>（<c>["face.", "approval."]</c>）；
    /// 一旦配置就<b>完全替换</b>默认值（不是追加，所以默认值里的前缀要保留须自己重复写出）。
    /// 不用「属性初始化为默认数组」是因为 .NET 配置绑定会把配置里的项追加到已有数组之后，没法替换。
    /// </summary>
    public string[]? SensitiveTypePrefixes { get; set; }

    /// <summary>发件箱记录创建后超过多少小时仍未发出，一律标 Skipped（stale）：避免关了又开推送时补发陈旧通知。小于 1 视为 1。</summary>
    public int StaleAfterHours { get; set; } = 24;

    // ---- 带下限的读取口径，调用方一律用这些，不直接读上面的原始值 ----

    internal int EffectiveMaxAttempts => Math.Max(1, MaxAttempts);

    internal int EffectiveBatchSize => Math.Clamp(BatchSize, 1, ExpoMaxMessagesPerRequest);

    internal int EffectiveReceiptDelayMinutes => Math.Max(1, ReceiptDelayMinutes);

    internal int EffectiveRetentionDays => Math.Max(1, DeliveryRetentionDays);

    internal int EffectivePollIntervalSeconds => Math.Max(1, PollIntervalSeconds);

    internal int EffectiveStaleAfterHours => Math.Max(1, StaleAfterHours);

    /// <summary>该通知类型是否敏感（以任一敏感前缀开头，忽略大小写；前缀为空白的配置项被忽略，避免空串匹配所有类型）。</summary>
    internal bool IsSensitiveType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        IEnumerable<string> prefixes = SensitiveTypePrefixes ?? DefaultSensitiveTypePrefixes;
        foreach (var prefix in prefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix)
                && type.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // record / 默认 ToString 不涉及；这里显式覆盖，保证任何日志解构都带不出 AccessToken。
    public override string ToString() =>
        $"ExpoPushOptions {{ Enabled = {Enabled}, Endpoint = {Endpoint}, AccessToken = {(string.IsNullOrWhiteSpace(AccessToken) ? "(none)" : "***")} }}";
}
