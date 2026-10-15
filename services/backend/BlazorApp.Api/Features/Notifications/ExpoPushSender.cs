using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>配置不可用（如 Endpoint 不是合法的 https 地址）。消息是固定文案，不含任何配置值，可以安全写日志。</summary>
internal sealed class ExpoPushConfigurationException(string message) : Exception(message);

/// <summary>一轮发送的汇总计数。日志只写这些计数与错误码，不写令牌、通知正文或异常消息。</summary>
internal sealed class ExpoPushRunStats
{
    public int Requests { get; set; }
    public int Claimed { get; set; }
    public int Accepted { get; set; }
    public int Retried { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Delivered { get; set; }
    public int ReceiptFailed { get; set; }
    public int ReceiptPending { get; set; }
    public int DevicesRevoked { get; set; }

    /// <summary>各短错误码出现次数（Expo 的 ticket / receipt 错误码、http-503、timeout、stale 等）。</summary>
    public Dictionary<string, int> Codes { get; } = new(StringComparer.Ordinal);

    public bool HadActivity =>
        Requests + Claimed + Accepted + Retried + Failed + Skipped + Delivered + ReceiptFailed + DevicesRevoked > 0;

    internal void CountCode(string code) => Codes[code] = Codes.GetValueOrDefault(code) + 1;

    public override string ToString() =>
        $"requests={Requests}, claimed={Claimed}, accepted={Accepted}, retried={Retried}, failed={Failed}, skipped={Skipped}, "
        + $"delivered={Delivered}, receiptFailed={ReceiptFailed}, receiptPending={ReceiptPending}, devicesRevoked={DevicesRevoked}, "
        + $"codes=[{string.Join(", ", Codes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}]";
}

/// <summary>
/// App 推送发送器：领取发件箱里待发送的记录 → 按 Expo 协议批量 <c>POST /send</c> → 按 ticket 更新状态；
/// 到点再批量 <c>POST /getReceipts</c> 取回执；并清理过期记录。可单测：HTTP 走 <see cref="IHttpClientFactory"/> 的命名客户端，
/// 时间走 <see cref="TimeProvider"/>，抖动可注入。
/// <para>
/// 语义是「至少一次」：Expo 已受理但本进程在写回结果前崩溃时，租约到期后会被重新领取并再发一次（可能重复弹出）。
/// 多实例安全靠逐行的条件 UPDATE 领取租约：<c>WHERE State = Pending AND NextAttemptAtUtc &lt;= now AND (LockedUntilUtc IS NULL OR LockedUntilUtc &lt; now)</c>，
/// 受影响行数为 1 才算领到，所以同一条记录同一时刻只会被一个实例发送。
/// </para>
/// <para>
/// 日志纪律：只写汇总计数与短错误码；不写令牌、通知标题与正文、AccessToken，不写异常的 Message
/// （网络 / JSON 异常只记类型名），Expo 回包 body 一律不记。
/// </para>
/// </summary>
internal sealed class ExpoPushSender(
    SqlSugarContext context,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ExpoPushOptions> options,
    TimeProvider timeProvider,
    ILogger<ExpoPushSender> logger
)
{
    /// <summary>命名 HttpClient；超时 30 秒、不跟随重定向，在 Program.cs 注册。</summary>
    internal const string HttpClientName = "expo-push";

    /// <summary>领取租约：覆盖「发一次 HTTP（≤ 30 秒）+ 写回结果」，崩溃后到期即可被重新领取。</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>取回执的请求失败时，这批记录推迟多久再试。</summary>
    internal static readonly TimeSpan ReceiptRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>Expo 回执只保留约 24 小时：发送超过该时长仍查不到回执的记录不再追。</summary>
    internal static readonly TimeSpan ReceiptWindow = TimeSpan.FromHours(24);

    /// <summary>已撤销的设备行保留多久后才清理（且已无发件箱引用）。</summary>
    internal static readonly TimeSpan RevokedDeviceRetention = TimeSpan.FromDays(90);

    /// <summary>一轮最多发多少批 / 取多少轮回执 / 清理多少批，避免积压时单轮占用过久。</summary>
    internal const int MaxSendBatchesPerRun = 20;
    internal const int MaxReceiptRoundsPerRun = 5;
    internal const int CleanupBatchSize = 500;
    internal const int MaxCleanupBatchesPerRun = 20;

    private const string ExperienceIdsErrorCode = "PUSH_TOO_MANY_EXPERIENCE_IDS";
    private const int MaxTicketIdLength = 64;
    private const int MaxErrorCodeLength = 64;

    private static readonly Regex SafeCodePattern = new(@"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ISqlSugarClient _db = context.Db;

    /// <summary>退避抖动的随机源（[0, 1)）；测试可替换成固定值。</summary>
    internal Func<double> JitterSource { get; set; } = () => Random.Shared.NextDouble();

    /// <summary>
    /// 跑一轮：发送到期的待发送记录 → 取到期的回执。<c>Enabled=false</c> 时什么都不做（不查库、不发请求）。
    /// 抛 <see cref="ExpoPushConfigurationException"/> 表示配置不可用；数据库异常（含迁移未执行）直接上抛，由调用方限频记录。
    /// </summary>
    public async Task<ExpoPushRunStats> RunOnceAsync(CancellationToken cancellationToken)
    {
        var stats = new ExpoPushRunStats();
        var current = options.CurrentValue;
        if (!current.Enabled)
        {
            return stats;
        }

        var endpoints = ResolveEndpoints(current);
        await SendPendingAsync(current, endpoints, stats, cancellationToken);
        await CheckReceiptsAsync(current, endpoints, stats, cancellationToken);

        if (stats.HadActivity)
        {
            logger.LogInformation("Expo 推送一轮完成: {Stats}", stats.ToString());
        }

        return stats;
    }

    // ------------------------------------------------------------------ 发送

    private async Task SendPendingAsync(
        ExpoPushOptions current,
        ExpoEndpoints endpoints,
        ExpoPushRunStats stats,
        CancellationToken cancellationToken
    )
    {
        var batchSize = current.EffectiveBatchSize;
        for (var round = 0; round < MaxSendBatchesPerRun; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now();
            var (claimed, candidateCount) = await ClaimBatchAsync(batchSize, now, cancellationToken);
            stats.Claimed += claimed.Count;
            if (claimed.Count > 0)
            {
                await ProcessClaimedAsync(claimed, current, endpoints, stats, cancellationToken);
            }

            // 候选不足一批说明已发完；候选满一批但一条都没领到（被别的实例抢光）也停，下一轮再来。
            if (candidateCount < batchSize || claimed.Count == 0)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 领取一批：先查候选，再对每一行单独做条件 UPDATE 抢租约，受影响行数为 1 才算领到。
    /// 候选查询不加锁（SqlSugar 默认带 NOLOCK 读）也没关系：能不能发以后面的条件 UPDATE 为准。
    /// </summary>
    internal async Task<(List<AppPushDelivery> Claimed, int CandidateCount)> ClaimBatchAsync(
        int batchSize,
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        var pending = AppPushDeliveryStates.Pending;
        var candidates = await _db.Queryable<AppPushDelivery>()
            .Where(delivery => delivery.State == pending
                && delivery.NextAttemptAtUtc <= now
                && (delivery.LockedUntilUtc == null || delivery.LockedUntilUtc < now))
            .OrderBy(delivery => delivery.NextAttemptAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var leaseUntil = now + LeaseDuration;
        var claimed = new List<AppPushDelivery>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var deliveryId = candidate.DeliveryId;
            var affected = await _db.Updateable<AppPushDelivery>()
                .SetColumns(delivery => delivery.LockedUntilUtc == leaseUntil)
                .Where(delivery => delivery.DeliveryId == deliveryId
                    && delivery.State == pending
                    && delivery.NextAttemptAtUtc <= now
                    && (delivery.LockedUntilUtc == null || delivery.LockedUntilUtc < now))
                .ExecuteCommandAsync(cancellationToken);
            if (affected == 1)
            {
                candidate.LockedUntilUtc = leaseUntil;
                claimed.Add(candidate);
            }
        }

        return (claimed, candidates.Count);
    }

    private async Task ProcessClaimedAsync(
        List<AppPushDelivery> claimed,
        ExpoPushOptions current,
        ExpoEndpoints endpoints,
        ExpoPushRunStats stats,
        CancellationToken cancellationToken
    )
    {
        var now = Now();
        var notificationIds = claimed.Select(delivery => delivery.NotificationId).Distinct().ToList();
        var deviceIds = claimed.Select(delivery => delivery.PushDeviceId).Distinct().ToList();
        var notifications = (await _db.Queryable<AppNotification>()
                .Where(notification => notificationIds.Contains(notification.NotificationId))
                .ToListAsync(cancellationToken))
            .ToDictionary(notification => notification.NotificationId, StringComparer.Ordinal);
        var devices = (await _db.Queryable<AppPushDevice>()
                .Where(device => deviceIds.Contains(device.PushDeviceId))
                .ToListAsync(cancellationToken))
            .ToDictionary(device => device.PushDeviceId, StringComparer.Ordinal);

        var staleBefore = now - TimeSpan.FromHours(current.EffectiveStaleAfterHours);
        var sendable = new List<(AppPushDelivery Row, ExpoPushMessage Message)>();
        foreach (var row in claimed)
        {
            notifications.TryGetValue(row.NotificationId, out var notification);
            devices.TryGetValue(row.PushDeviceId, out var device);
            var skipReason = DecideSkip(row, notification, device, staleBefore, now);
            if (skipReason != null)
            {
                await MarkSkippedAsync(row, skipReason, cancellationToken);
                stats.Skipped++;
                stats.CountCode(skipReason);
                continue;
            }

            sendable.Add((row, ExpoPushContent.Build(notification!, device!.ExpoPushToken, current, now)));
        }

        if (sendable.Count == 0)
        {
            return;
        }

        var outcome = await PostSendAsync(endpoints, current, sendable.Select(item => item.Message).ToList(), stats, cancellationToken);
        if (outcome.SplitByProject && sendable.Count > 1)
        {
            // 一批里混了不同 Expo 项目的令牌（本仓库有 hbweb-expo 与 hb-pos-handheld 两个 EAS 项目）：Expo 要求同一请求只能属于同一项目，
            // 退化成逐条发送，每条自己成一个请求，混批只影响效率、不丢消息。
            foreach (var item in sendable)
            {
                var single = await PostSendAsync(endpoints, current, [item.Message], stats, cancellationToken);
                await ApplySendOutcomeAsync([item.Row], single, current, stats, cancellationToken);
            }

            return;
        }

        await ApplySendOutcomeAsync(sendable.Select(item => item.Row).ToList(), outcome, current, stats, cancellationToken);
    }

    /// <summary>发送前的跳过判定；返回短原因码，null 表示可以发。</summary>
    internal static string? DecideSkip(
        AppPushDelivery row,
        AppNotification? notification,
        AppPushDevice? device,
        DateTime staleBefore,
        DateTime now
    )
    {
        if (row.CreatedAtUtc < staleBefore)
        {
            return "stale";
        }

        if (notification == null)
        {
            return "notification-missing";
        }

        if (notification.RevokedAtUtc != null)
        {
            return "revoked";
        }

        if (notification.ExpiresAtUtc != null && notification.ExpiresAtUtc <= now)
        {
            return "expired";
        }

        if (device == null)
        {
            return "device-missing";
        }

        // 设备被撤销（退出登录 / 转给别的账号 / 令牌失效 / 超限）后，之前排队的通知不能再发到这个令牌：
        // 手持换人登录时，上一个人的通知绝不能弹到下一个人手里。
        return device.RevokedAtUtc != null ? "device-revoked" : null;
    }

    private async Task ApplySendOutcomeAsync(
        IReadOnlyList<AppPushDelivery> rows,
        SendOutcome outcome,
        ExpoPushOptions current,
        ExpoPushRunStats stats,
        CancellationToken cancellationToken
    )
    {
        if (outcome.Tickets == null)
        {
            // 整批失败（网络 / 429 / 5xx / 回包不可解析 → 重试；其他 4xx → 失败）。
            var code = outcome.FailureCode ?? "unknown";
            if (outcome.CredentialsRejected)
            {
                logger.LogError("Expo 推送凭据被拒绝（HTTP {Status}），请检查 Push:Expo:AccessToken / EAS 推送凭据", outcome.HttpStatus);
            }

            foreach (var row in rows)
            {
                stats.CountCode(code);
                if (outcome.Retryable)
                {
                    await RetryOrFailAsync(row, code, current, stats, cancellationToken);
                }
                else
                {
                    await FailAsync(row, code, stats, cancellationToken);
                }
            }

            return;
        }

        var credentialLogged = false;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var ticket = outcome.Tickets[index];
            if (ticket.Ok)
            {
                await MarkSentAsync(row, ticket.Id, current, cancellationToken);
                stats.Accepted++;
                continue;
            }

            var code = ticket.ErrorCode ?? "unknown";
            stats.CountCode(code);
            switch (code)
            {
                case "DeviceNotRegistered":
                    // 令牌已失效（卸载 / 重装 / 关闭通知）：撤销该设备，这条记录失败，之前排队的其他记录发送时会因设备已撤销而跳过。
                    stats.DevicesRevoked += await RevokeDeviceAsync(row.PushDeviceId, cancellationToken);
                    await FailAsync(row, code, stats, cancellationToken);
                    break;
                case "MessageRateExceeded":
                    await RetryOrFailAsync(row, code, current, stats, cancellationToken);
                    break;
                case "InvalidCredentials":
                    if (!credentialLogged)
                    {
                        credentialLogged = true;
                        logger.LogError("Expo 推送凭据无效（InvalidCredentials），请检查 Push:Expo:AccessToken 与 EAS 上登记的 APNs / FCM 凭据");
                    }

                    await FailAsync(row, code, stats, cancellationToken);
                    break;
                default:
                    // MessageTooBig 及其他未知错误：重试也不会好，直接失败，只记错误码。
                    await FailAsync(row, code, stats, cancellationToken);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ 回执

    private async Task CheckReceiptsAsync(
        ExpoPushOptions current,
        ExpoEndpoints endpoints,
        ExpoPushRunStats stats,
        CancellationToken cancellationToken
    )
    {
        var sent = AppPushDeliveryStates.Sent;
        for (var round = 0; round < MaxReceiptRoundsPerRun; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now();
            var due = await _db.Queryable<AppPushDelivery>()
                .Where(delivery => delivery.State == sent && delivery.NextAttemptAtUtc <= now)
                .OrderBy(delivery => delivery.NextAttemptAtUtc)
                .Take(ExpoPushOptions.ExpoMaxReceiptIdsPerRequest)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
            {
                return;
            }

            // 没有 ticket id 的已受理记录（异常数据）没法查回执：直接按已送达收尾，免得它永远停在 Sent。
            var withTicket = new List<AppPushDelivery>(due.Count);
            foreach (var row in due)
            {
                if (string.IsNullOrEmpty(row.TicketId))
                {
                    await SetReceiptResultAsync(row, AppPushDeliveryStates.Delivered, "no-ticket", Now(), cancellationToken);
                    stats.Delivered++;
                }
                else
                {
                    withTicket.Add(row);
                }
            }

            if (withTicket.Count > 0)
            {
                await CheckReceiptBatchAsync(withTicket, current, endpoints, stats, cancellationToken);
            }

            if (due.Count < ExpoPushOptions.ExpoMaxReceiptIdsPerRequest)
            {
                return;
            }
        }
    }

    private async Task CheckReceiptBatchAsync(
        List<AppPushDelivery> rows,
        ExpoPushOptions current,
        ExpoEndpoints endpoints,
        ExpoPushRunStats stats,
        CancellationToken cancellationToken
    )
    {
        var reply = await PostAsync(
            endpoints.Receipts,
            ExpoPushContent.SerializeReceiptsBody(rows.Select(row => row.TicketId!).ToList()),
            current,
            stats,
            cancellationToken);

        Dictionary<string, ReceiptResult>? receipts = null;
        if (reply.NetworkErrorCode == null && reply.StatusCode is >= 200 and < 300 && reply.Body != null)
        {
            receipts = ParseReceipts(reply.Body);
        }

        var now = Now();
        if (receipts == null)
        {
            // 取回执失败（网络 / 非 2xx / 回包不可解析）：整批推迟 5 分钟再试，不改状态也不计重试次数。
            var code = reply.NetworkErrorCode ?? (reply.StatusCode is int status ? $"http-{status}" : "bad-response");
            stats.CountCode($"receipt-{code}");
            stats.ReceiptPending += rows.Count;
            var retryAt = now + ReceiptRetryDelay;
            var ids = rows.Select(row => row.DeliveryId).ToList();
            var sent = AppPushDeliveryStates.Sent;
            await _db.Updateable<AppPushDelivery>()
                .SetColumns(delivery => delivery.NextAttemptAtUtc == retryAt)
                .Where(delivery => ids.Contains(delivery.DeliveryId) && delivery.State == sent)
                .ExecuteCommandAsync(cancellationToken);
            return;
        }

        foreach (var row in rows)
        {
            if (receipts.TryGetValue(row.TicketId!, out var receipt))
            {
                if (receipt.Ok)
                {
                    await SetReceiptResultAsync(row, AppPushDeliveryStates.Delivered, null, now, cancellationToken);
                    stats.Delivered++;
                    continue;
                }

                var code = receipt.ErrorCode ?? "unknown";
                stats.CountCode(code);
                if (code == "DeviceNotRegistered")
                {
                    stats.DevicesRevoked += await RevokeDeviceAsync(row.PushDeviceId, cancellationToken);
                }

                await SetReceiptResultAsync(row, AppPushDeliveryStates.Failed, code, now, cancellationToken);
                stats.ReceiptFailed++;
                continue;
            }

            // 回执里没有该 id：Expo 只保留约 24 小时。已超过窗口就不再追（按已送达收尾）；否则过一阵再查。
            var sentAt = row.SentAtUtc ?? row.CreatedAtUtc;
            if (sentAt + ReceiptWindow <= now)
            {
                await SetReceiptResultAsync(row, AppPushDeliveryStates.Delivered, "receipt-expired", now, cancellationToken);
                stats.Delivered++;
            }
            else
            {
                var recheckAt = now + TimeSpan.FromMinutes(current.EffectiveReceiptDelayMinutes);
                var deliveryId = row.DeliveryId;
                var sentState = AppPushDeliveryStates.Sent;
                await _db.Updateable<AppPushDelivery>()
                    .SetColumns(delivery => delivery.NextAttemptAtUtc == recheckAt)
                    .Where(delivery => delivery.DeliveryId == deliveryId && delivery.State == sentState)
                    .ExecuteCommandAsync(cancellationToken);
                stats.ReceiptPending++;
            }
        }
    }

    // ------------------------------------------------------------------ 清理

    /// <summary>
    /// 清理（分批、每批限定条数；DELETE 一律带明确的状态与时间条件）：
    /// ① 已结束（Delivered / Failed / Skipped）且创建时间早于保留期的发件箱记录；
    /// ② 撤销时间早于 90 天、且已无任何发件箱记录引用的设备行。返回 (删除的发件箱行数, 删除的设备行数)。
    /// </summary>
    public async Task<(int Deliveries, int Devices)> CleanupAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!current.Enabled)
        {
            return (0, 0);
        }

        var now = Now();
        var deliveryCutoff = now - TimeSpan.FromDays(current.EffectiveRetentionDays);
        var finishedStates = new[] { AppPushDeliveryStates.Delivered, AppPushDeliveryStates.Failed, AppPushDeliveryStates.Skipped };
        var deletedDeliveries = 0;
        for (var batch = 0; batch < MaxCleanupBatchesPerRun; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await _db.Queryable<AppPushDelivery>()
                .Where(delivery => finishedStates.Contains(delivery.State) && delivery.CreatedAtUtc < deliveryCutoff)
                .Take(CleanupBatchSize)
                .Select(delivery => delivery.DeliveryId)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            // 删除时重复状态与时间条件：候选查询之后状态不可能倒退，但 DELETE 本身必须自带边界，不能只靠 Id 列表。
            deletedDeliveries += await _db.Deleteable<AppPushDelivery>()
                .Where(delivery => ids.Contains(delivery.DeliveryId)
                    && finishedStates.Contains(delivery.State)
                    && delivery.CreatedAtUtc < deliveryCutoff)
                .ExecuteCommandAsync(cancellationToken);
            if (ids.Count < CleanupBatchSize)
            {
                break;
            }
        }

        var deviceCutoff = now - RevokedDeviceRetention;
        var deletedDevices = 0;
        for (var batch = 0; batch < MaxCleanupBatchesPerRun; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateIds = await _db.Queryable<AppPushDevice>()
                .Where(device => device.RevokedAtUtc != null && device.RevokedAtUtc < deviceCutoff)
                .Take(CleanupBatchSize)
                .Select(device => device.PushDeviceId)
                .ToListAsync(cancellationToken);
            if (candidateIds.Count == 0)
            {
                break;
            }

            var referenced = await _db.Queryable<AppPushDelivery>()
                .Where(delivery => candidateIds.Contains(delivery.PushDeviceId))
                .Select(delivery => delivery.PushDeviceId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var deletable = candidateIds.Except(referenced, StringComparer.Ordinal).ToList();
            if (deletable.Count > 0)
            {
                deletedDevices += await _db.Deleteable<AppPushDevice>()
                    .Where(device => deletable.Contains(device.PushDeviceId)
                        && device.RevokedAtUtc != null
                        && device.RevokedAtUtc < deviceCutoff)
                    .ExecuteCommandAsync(cancellationToken);
            }

            // 这一批候选里还有被发件箱引用的（暂时删不掉），再往后翻只会反复取到同一批：到此为止，等它们的发件箱记录过期后下一轮再清。
            if (candidateIds.Count < CleanupBatchSize || deletable.Count < candidateIds.Count)
            {
                break;
            }
        }

        if (deletedDeliveries + deletedDevices > 0)
        {
            logger.LogInformation(
                "Expo 推送清理完成: Deliveries={Deliveries}, Devices={Devices}",
                deletedDeliveries,
                deletedDevices);
        }

        return (deletedDeliveries, deletedDevices);
    }

    // ------------------------------------------------------------------ 行状态写回（全部带 State 条件，防止与别的实例互相覆盖）

    private async Task MarkSentAsync(AppPushDelivery row, string? ticketId, ExpoPushOptions current, CancellationToken cancellationToken)
    {
        var now = Now();
        var deliveryId = row.DeliveryId;
        var pending = AppPushDeliveryStates.Pending;
        var sent = AppPushDeliveryStates.Sent;
        var attempts = row.Attempts + 1;
        var receiptAt = now + TimeSpan.FromMinutes(current.EffectiveReceiptDelayMinutes);
        var safeTicket = ticketId is { Length: > 0 } ? ticketId[..Math.Min(ticketId.Length, MaxTicketIdLength)] : null;
        await _db.Updateable<AppPushDelivery>()
            .SetColumns(delivery => delivery.State == sent)
            .SetColumns(delivery => delivery.Attempts == attempts)
            .SetColumns(delivery => delivery.TicketId == safeTicket)
            .SetColumns(delivery => delivery.SentAtUtc == now)
            .SetColumns(delivery => delivery.NextAttemptAtUtc == receiptAt)
            .SetColumns(delivery => delivery.LockedUntilUtc == null)
            .SetColumns(delivery => delivery.LastError == null)
            .Where(delivery => delivery.DeliveryId == deliveryId && delivery.State == pending)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>发送失败后的重试判定：累计失败次数 +1；达到 MaxAttempts 标 Failed，否则按退避表安排下次尝试。</summary>
    private async Task RetryOrFailAsync(
        AppPushDelivery row,
        string code,
        ExpoPushOptions current,
        ExpoPushRunStats stats,
        CancellationToken cancellationToken
    )
    {
        var attempts = row.Attempts + 1;
        if (attempts >= current.EffectiveMaxAttempts)
        {
            await FailAsync(row, code, stats, cancellationToken);
            return;
        }

        var nextAttemptAt = Now() + ExpoPushBackoff.Delay(attempts, JitterSource());
        var deliveryId = row.DeliveryId;
        var pending = AppPushDeliveryStates.Pending;
        var safeCode = SafeCode(code);
        await _db.Updateable<AppPushDelivery>()
            .SetColumns(delivery => delivery.Attempts == attempts)
            .SetColumns(delivery => delivery.NextAttemptAtUtc == nextAttemptAt)
            .SetColumns(delivery => delivery.LockedUntilUtc == null)
            .SetColumns(delivery => delivery.LastError == safeCode)
            .Where(delivery => delivery.DeliveryId == deliveryId && delivery.State == pending)
            .ExecuteCommandAsync(cancellationToken);
        stats.Retried++;
    }

    private async Task FailAsync(AppPushDelivery row, string code, ExpoPushRunStats stats, CancellationToken cancellationToken)
    {
        var attempts = row.Attempts + 1;
        var deliveryId = row.DeliveryId;
        var pending = AppPushDeliveryStates.Pending;
        var failed = AppPushDeliveryStates.Failed;
        var safeCode = SafeCode(code);
        await _db.Updateable<AppPushDelivery>()
            .SetColumns(delivery => delivery.State == failed)
            .SetColumns(delivery => delivery.Attempts == attempts)
            .SetColumns(delivery => delivery.LockedUntilUtc == null)
            .SetColumns(delivery => delivery.LastError == safeCode)
            .Where(delivery => delivery.DeliveryId == deliveryId && delivery.State == pending)
            .ExecuteCommandAsync(cancellationToken);
        stats.Failed++;
    }

    private async Task MarkSkippedAsync(AppPushDelivery row, string reason, CancellationToken cancellationToken)
    {
        var deliveryId = row.DeliveryId;
        var pending = AppPushDeliveryStates.Pending;
        var skipped = AppPushDeliveryStates.Skipped;
        await _db.Updateable<AppPushDelivery>()
            .SetColumns(delivery => delivery.State == skipped)
            .SetColumns(delivery => delivery.LockedUntilUtc == null)
            .SetColumns(delivery => delivery.LastError == reason)
            .Where(delivery => delivery.DeliveryId == deliveryId && delivery.State == pending)
            .ExecuteCommandAsync(cancellationToken);
    }

    private async Task SetReceiptResultAsync(
        AppPushDelivery row,
        int newState,
        string? code,
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        var deliveryId = row.DeliveryId;
        var sent = AppPushDeliveryStates.Sent;
        var safeCode = code == null ? null : SafeCode(code);
        await _db.Updateable<AppPushDelivery>()
            .SetColumns(delivery => delivery.State == newState)
            .SetColumns(delivery => delivery.ReceiptCheckedAtUtc == now)
            .SetColumns(delivery => delivery.LastError == safeCode)
            .Where(delivery => delivery.DeliveryId == deliveryId && delivery.State == sent)
            .ExecuteCommandAsync(cancellationToken);
    }

    private Task<int> RevokeDeviceAsync(string pushDeviceId, CancellationToken cancellationToken) =>
        AppPushDeviceService.RevokeAsync(
            _db,
            [pushDeviceId],
            AppPushRevokeReasons.DeviceNotRegistered,
            Now(),
            cancellationToken);

    // ------------------------------------------------------------------ HTTP 与解析

    private async Task<SendOutcome> PostSendAsync(
        ExpoEndpoints endpoints,
        ExpoPushOptions current,
        IReadOnlyList<ExpoPushMessage> messages,
        ExpoPushRunStats stats,
        CancellationToken cancellationToken
    )
    {
        var reply = await PostAsync(endpoints.Send, ExpoPushContent.SerializeSendBody(messages), current, stats, cancellationToken);
        return Classify(reply, messages.Count);
    }

    /// <summary>把 HTTP 结果归类为：可解析的 tickets / 可重试的整批失败 / 不可重试的整批失败 / 需要拆批。纯函数，便于单测。</summary>
    internal static SendOutcome Classify(HttpReply reply, int expectedTickets)
    {
        if (reply.NetworkErrorCode != null)
        {
            return SendOutcome.Retry(reply.NetworkErrorCode, null);
        }

        var status = reply.StatusCode ?? 0;
        if (status is >= 200 and < 300)
        {
            var tickets = reply.Body == null ? null : ParseTickets(reply.Body, expectedTickets);
            // 2xx 但回包不是预期结构：不知道哪些被受理，保守地重试（极端情况下可能重复，好过丢）。
            return tickets == null ? SendOutcome.Retry("bad-response", status) : SendOutcome.WithTickets(tickets, status);
        }

        if (status == (int)HttpStatusCode.TooManyRequests || status == (int)HttpStatusCode.RequestTimeout || status >= 500)
        {
            return SendOutcome.Retry($"http-{status}", status);
        }

        if (status == (int)HttpStatusCode.BadRequest && reply.Body != null && HasErrorCode(reply.Body, ExperienceIdsErrorCode))
        {
            return SendOutcome.SplitRequest(status);
        }

        var rejected = status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden;
        return SendOutcome.Fail($"http-{status}", status, rejected);
    }

    private async Task<HttpReply> PostAsync(
        Uri uri,
        string json,
        ExpoPushOptions current,
        ExpoPushRunStats stats,
        CancellationToken cancellationToken
    )
    {
        stats.Requests++;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(current.AccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken.Trim());
            }

            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new HttpReply((int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // 不是调用方取消：HttpClient 的 30 秒超时。
            return new HttpReply(null, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new HttpReply(null, null, "network-error");
        }
    }

    internal static List<ExpoTicket>? ParseTickets(string body, int expected)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() != expected)
            {
                return null;
            }

            var tickets = new List<ExpoTicket>(expected);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var status = GetString(item, "status");
                if (status == "ok")
                {
                    tickets.Add(new ExpoTicket(true, GetString(item, "id"), null));
                }
                else
                {
                    tickets.Add(new ExpoTicket(false, null, ReadErrorCode(item)));
                }
            }

            return tickets;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Dictionary<string, ReceiptResult>? ParseReceipts(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var receipts = new Dictionary<string, ReceiptResult>(StringComparer.Ordinal);
            foreach (var property in data.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                receipts[property.Name] = GetString(property.Value, "status") == "ok"
                    ? new ReceiptResult(true, null)
                    : new ReceiptResult(false, ReadErrorCode(property.Value));
            }

            return receipts;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasErrorCode(string body, string code)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.EnumerateArray().Any(error =>
                    error.ValueKind == JsonValueKind.Object && GetString(error, "code") == code);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>错误码取 <c>details.error</c>（如 DeviceNotRegistered）；取不到或含非法字符时记 <c>unknown</c>，绝不把回包文本写进库或日志。</summary>
    private static string ReadErrorCode(JsonElement element)
    {
        if (element.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            var code = GetString(details, "error");
            if (code != null && SafeCodePattern.IsMatch(code))
            {
                return code;
            }
        }

        return "unknown";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string SafeCode(string code) =>
        SafeCodePattern.IsMatch(code) ? code : "unknown";

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static ExpoEndpoints ResolveEndpoints(ExpoPushOptions current)
    {
        var raw = current.Endpoint?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(raw)
            || !Uri.TryCreate(raw + "/", UriKind.Absolute, out var baseUri)
            || !(baseUri.Scheme == Uri.UriSchemeHttps || (baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)))
        {
            // AccessToken 会放进 Authorization 头：只允许 https（本机回环地址除外，供联调），不允许明文传输。
            throw new ExpoPushConfigurationException("Push:Expo:Endpoint 必须是 https 地址（本机回环地址可用 http）。");
        }

        return new ExpoEndpoints(new Uri(baseUri, "send"), new Uri(baseUri, "getReceipts"));
    }

    internal sealed record ExpoEndpoints(Uri Send, Uri Receipts);

    internal sealed record HttpReply(int? StatusCode, string? Body, string? NetworkErrorCode);

    internal readonly record struct ExpoTicket(bool Ok, string? Id, string? ErrorCode);

    internal sealed record ReceiptResult(bool Ok, string? ErrorCode);

    internal sealed record SendOutcome(
        IReadOnlyList<ExpoTicket>? Tickets,
        bool Retryable,
        string? FailureCode,
        int? HttpStatus,
        bool CredentialsRejected,
        bool SplitByProject
    )
    {
        internal static SendOutcome WithTickets(IReadOnlyList<ExpoTicket> tickets, int status) =>
            new(tickets, false, null, status, false, false);

        internal static SendOutcome Retry(string code, int? status) => new(null, true, code, status, false, false);

        internal static SendOutcome Fail(string code, int status, bool credentialsRejected) =>
            new(null, false, code, status, credentialsRejected, false);

        internal static SendOutcome SplitRequest(int status) => new(null, false, null, status, false, true);
    }
}
