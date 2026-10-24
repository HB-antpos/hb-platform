using System.Globalization;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 提醒未提交的分店（方案 3.3）：找出这张订单表开启、但订单还没提交的分店，发 App 通知给「下单人员授权」里能替这些店下单的人
/// （一人管几家店就合并成一条）。截止前 72 小时、24 小时各自动发一次；采购也可以随时手动发。
/// 防重复靠通知平台的幂等键：自动提醒的键带截止时间与档位，改了截止时间会重新提醒；手动提醒按分钟去重，防连点。
/// </summary>
public sealed class SupplierPreorderReminderService
{
    /// <summary>自动提醒的档位（距截止）。</summary>
    internal static readonly TimeSpan[] AutoTiers = { TimeSpan.FromHours(72), TimeSpan.FromHours(24) };

    private readonly ISqlSugarClient _db;
    private readonly INotificationPublisher _publisher;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public SupplierPreorderReminderService(ISqlSugarClient db, INotificationPublisher publisher, ILogger logger, TimeProvider? timeProvider = null)
    {
        _db = db;
        _publisher = publisher;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime NowUtc => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>现在落在哪个自动档：剩余 ≤ 24 小时 → 24h；≤ 72 小时 → 72h；已过截止或还早 → null。错过 72h 档时直接发 24h 档，不补发。</summary>
    public static TimeSpan? DueTier(DateTime deadlineUtc, DateTime nowUtc)
    {
        var remaining = deadlineUtc - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        return AutoTiers.Where(t => remaining <= t).OrderBy(t => t).Cast<TimeSpan?>().FirstOrDefault();
    }

    /// <summary>按人合并：每个下单人员 → 他能代下、且还没提交的分店名（按分店代码排序）。</summary>
    public static Dictionary<string, List<string>> GroupRecipients(
        IEnumerable<(string StoreGuid, string StoreCode, string StoreName)> pendingStores,
        IEnumerable<(string UserGuid, string StoreGuid)> orderers)
    {
        var stores = pendingStores.GroupBy(s => s.StoreGuid, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return orderers
            .Where(o => stores.ContainsKey(o.StoreGuid))
            .GroupBy(o => o.UserGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(o => stores[o.StoreGuid]).DistinctBy(s => s.StoreGuid, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s.StoreCode, StringComparer.OrdinalIgnoreCase).Select(s => s.StoreName).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>英文正文（与通知平台其他类型一致），只含订单表名称、分店名与截止时间（悉尼）。</summary>
    public static (string Title, string Body) BuildMessage(string formTitle, IReadOnlyList<string> storeNames, DateTime? deadlineUtc)
    {
        var stores = storeNames.Count <= 3 ? string.Join(", ", storeNames) : $"{string.Join(", ", storeNames.Take(3))} and {storeNames.Count - 3} more";
        var verb = storeNames.Count == 1 ? "has" : "have";
        var deadline = deadlineUtc is null ? string.Empty : $" Deadline: {FormatSydney(deadlineUtc.Value)} (Sydney).";
        var title = $"Preorder not submitted: {formTitle}";
        var body = $"{stores} {verb} not submitted the {formTitle} preorder yet.{deadline} Please submit it in HB admin (Preorder ordering).";
        return (title.Length > 200 ? title[..200] : title, body.Length > 1000 ? body[..1000] : body);
    }

    private static string FormatSydney(DateTime utc)
    {
        var zone = ResolveSydney();
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return local.ToString("ddd d MMM HH:mm", CultureInfo.GetCultureInfo("en-AU"));
    }

    private static TimeZoneInfo ResolveSydney()
    {
        foreach (var id in new[] { "Australia/Sydney", "AUS Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("AEST", TimeSpan.FromHours(10), "AEST", "AEST");
    }

    /// <summary>给一张订单表的未提交分店发提醒；dedupeSuffix 区分自动档位 / 手动时刻。</summary>
    public async Task<SupplierPreorderRemindResultDto> RemindAsync(SupplierPreorderForm form, string dedupeSuffix, string createdBy, CancellationToken cancellationToken = default)
    {
        var enabled = await _db.Queryable<SupplierPreorderFormStore>().Where(s => s.FormGuid == form.FormGuid).ToListAsync();
        var submitted = (await _db.Queryable<SupplierPreorderStoreOrder>()
                .Where(o => o.FormGuid == form.FormGuid && o.Status == SupplierPreorderStoreOrderStatuses.Submitted)
                .Select(o => o.StoreGuid)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = enabled.Where(s => !submitted.Contains(s.StoreGuid)).ToList();
        var result = new SupplierPreorderRemindResultDto { PendingStoreCount = pending.Count };
        if (pending.Count == 0)
        {
            return result;
        }

        var pendingGuids = pending.Select(s => s.StoreGuid).ToList();
        var orderers = await _db.Queryable<SupplierPreorderOrdererStore>()
            .Where(o => pendingGuids.Contains(o.StoreGuid))
            .Select(o => new { o.UserGuid, o.StoreGuid })
            .ToListAsync();
        var covered = orderers.Select(o => o.StoreGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.StoresWithoutOrderers = pending.Where(s => !covered.Contains(s.StoreGuid)).OrderBy(s => s.StoreCode).Select(s => s.StoreName).ToList();

        var groups = GroupRecipients(
            pending.Select(s => (s.StoreGuid, s.StoreCode, s.StoreName)),
            orderers.Select(o => (o.UserGuid, o.StoreGuid)));
        var deadline = form.DeadlineAtUtc is null ? (DateTime?)null : DateTime.SpecifyKind(form.DeadlineAtUtc.Value, DateTimeKind.Utc);
        foreach (var (userGuid, storeNames) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (title, body) = BuildMessage(form.Title, storeNames, deadline);
            var published = await _publisher.PublishAsync(new NotificationPublishRequest
            {
                Type = AppNotificationTypes.SupplierPreorderReminder,
                Title = title,
                Body = body,
                Audience = NotificationAudience.ForUsers(userGuid),
                Data = new Dictionary<string, string> { ["formGuid"] = form.FormGuid },
                DedupeKey = $"supplier-preorder:{form.FormGuid}:{userGuid}:{dedupeSuffix}",
                ExpiresAtUtc = deadline,
                CreatedBy = createdBy,
            }, cancellationToken);
            if (!published.Deduplicated && published.RecipientCount > 0)
            {
                result.NotifiedUserCount++;
            }
        }

        return result;
    }

    /// <summary>后台任务：进行中、72 小时内截止的订单表，按档位自动提醒一次。返回发出提醒的订单表数。</summary>
    public async Task<int> RunAutoAsync(CancellationToken cancellationToken)
    {
        if (!_db.DbMaintenance.IsAnyTable("SupplierPreorderForm", false))
        {
            return 0;
        }

        var now = NowUtc;
        var horizon = now + AutoTiers.Max();
        var forms = await _db.Queryable<SupplierPreorderForm>()
            .Where(f => !f.IsDeleted && f.Status == SupplierPreorderFormStatuses.Open && f.DeadlineAtUtc != null && f.DeadlineAtUtc > now && f.DeadlineAtUtc <= horizon)
            .ToListAsync();
        var sent = 0;
        foreach (var form in forms)
        {
            var deadline = DateTime.SpecifyKind(form.DeadlineAtUtc!.Value, DateTimeKind.Utc);
            var tier = DueTier(deadline, now);
            if (tier is null)
            {
                continue;
            }

            try
            {
                var suffix = $"auto-{(int)tier.Value.TotalHours}h-{deadline:yyyyMMddHHmm}";
                var result = await RemindAsync(form, suffix, "system:supplier-preorder", cancellationToken);
                if (result.NotifiedUserCount > 0)
                {
                    sent++;
                    _logger.LogInformation("供应商预订单截止前 {Tier} 小时提醒：{FormGuid}，未提交 {Stores} 家，通知 {Users} 人",
                        (int)tier.Value.TotalHours, form.FormGuid, result.PendingStoreCount, result.NotifiedUserCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "供应商预订单截止提醒失败：{FormGuid}", form.FormGuid);
            }
        }

        return sent;
    }
}
