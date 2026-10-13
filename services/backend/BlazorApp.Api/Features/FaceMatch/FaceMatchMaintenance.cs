using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 人脸比对的后台维护任务（每个任务一个方法，单独可测，时间全部来自 <see cref="TimeProvider"/>）：
/// 1. 访客库清理：门店本地午夜之后删库（DeleteGroup），更新 FaceVisitGroup；另扫描腾讯云里遗漏的过期库；
/// 2. 员工库完整性：对比腾讯云员工库人数与有效登记数，不一致写审计并记 Warning；
/// 3. Capture 记录保留清理：FaceCaptureEvent / FaceCaptureImage 超过保留天数的行；
/// 4. 名单条目：到期自动停用 + DeletePerson + 删参考照片、到期前 30 天提醒、清理重试；
/// 5. 命中图到期删除；
/// 6. 撤回后的补偿：同意已撤回但腾讯云删除未完成的登记、已撤回登记留下的照片；
/// 7. 地域改动后作废旧的跨境披露审查（只清空审查记录并写审计，不再关闭任何档案；缺口在页面显示为警告）；
/// 8. 高相似度命中自动确认（门店阈值 UnwelcomeAutoConfirmScore，默认 90，0 = 关闭；用户 2026-10-13 决定）。
/// 空转原则：没有管理凭据时不发任何腾讯云请求；没有门店启用（所有档案都是 Off 或没有档案）时不做读取类与保留清理类工作。
/// 已存在数据的删除类补偿（过期访客库、撤回后的清理）不依赖「有门店启用」，否则门店关闭后数据会无人清理；没有可删的行时它们只是一条廉价查询。
/// </summary>
public sealed class FaceMatchMaintenance(
    SqlSugarContext context,
    IFaceLibrary library,
    IFaceMatchObjectStore objectStore,
    IFaceMatchSettingsProvider settingsProvider,
    FaceStaffService staffService,
    FaceUnwelcomeService unwelcomeService,
    INotificationPublisher notificationPublisher,
    IFaceMatchAudit audit,
    TimeProvider timeProvider,
    ILogger<FaceMatchMaintenance> logger)
{
    public static readonly FaceMatchActor SystemActor = new(FaceMatchAuditActions.SystemActorId, "system");

    private const int BatchSize = 50;
    private const int RetentionBatchSize = 500;
    private const int RetentionMaxBatches = 20;
    private const int OrphanGroupMinAgeDays = 2;

    private static readonly Regex VisitGroupPattern = new(@"^v(?<store>.+)_(?<date>\d{8})\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ISqlSugarClient _db = context.Db;

    /// <summary>至少有一个门店档案不是 Off（有门店启用）。</summary>
    public async Task<bool> AnyStoreEnabledAsync(CancellationToken cancellationToken)
    {
        return await _db.Queryable<FaceStoreProfile>().AnyAsync(p => p.Mode != FaceStoreMode.Off, cancellationToken);
    }

    // ---------- 8. 高相似度命中自动确认 ----------

    /// <summary>
    /// 命中任务自动确认（用户 2026-10-13 决定）：相似度达到或超过门店阈值（FaceStoreProfile.UnwelcomeAutoConfirmScore，
    /// 默认 90，0 = 关闭）的任务不再等人工复核，由系统直接标为 Confirmed（复核人记为 system，页面显示「自动确认」）。
    /// 1. 只处理已经提示过店员（NotifiedAtUtc 非空）、仍待复核、没人判断过、命中图还在的任务——提示一定先发出去，自动确认只是替人结案；
    /// 2. 条件更新（仍是 Pending 且没有复核人才写）：与人工复核并发时谁先写谁生效，不会覆盖人工结论；
    /// 3. 每个任务写一条审计（Result = auto:confirmed;min=阈值）；单条失败只记日志，下一轮重试；
    /// 4. 「已确认」只是任务状态：系统不会因此拒绝服务或通知警方。
    /// 返回本轮自动确认的任务数。
    /// </summary>
    public async Task<int> AutoConfirmHitsAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var pendingStatus = FaceMatchReviewStatus.Pending;
        var tasks = await _db.Queryable<FaceMatchTask>()
            .Where(t => t.ReviewStatus == pendingStatus
                && t.FirstReviewerId == null
                && t.NotifiedAtUtc != null
                && t.ImageDeleteAtUtc > nowUtc)
            .OrderBy(t => t.CapturedAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return 0;
        }

        // 每店阈值：没有档案行（理论上不会有任务）按默认值。
        var storeCodes = tasks.Select(t => t.StoreCode).Distinct().ToList();
        var thresholds = (await _db.Queryable<FaceStoreProfile>()
                .Where(p => storeCodes.Contains(p.StoreCode))
                .Select(p => new { p.StoreCode, p.UnwelcomeAutoConfirmScore })
                .ToListAsync(cancellationToken))
            .ToDictionary(p => p.StoreCode, p => p.UnwelcomeAutoConfirmScore);

        var confirmed = FaceMatchReviewStatus.Confirmed;
        var actorId = SystemActor.Id;
        var handled = 0;
        foreach (var task in tasks)
        {
            var threshold = thresholds.TryGetValue(task.StoreCode, out var value)
                ? value
                : FaceStoreProfile.DefaultUnwelcomeAutoConfirmScore;
            if (threshold <= 0m || task.Score < threshold)
            {
                continue;
            }

            try
            {
                // 表达式里只放局部变量：SqlSugar 会把三元表达式 / 方法调用翻译成 SQL。
                var rows = await _db.Updateable<FaceMatchTask>()
                    .SetColumns(t => new FaceMatchTask
                    {
                        FirstReviewerId = actorId,
                        FirstReviewedAtUtc = nowUtc,
                        ReviewStatus = confirmed,
                    })
                    .Where(t => t.TaskId == task.TaskId && t.ReviewStatus == pendingStatus && t.FirstReviewerId == null)
                    .ExecuteCommandAsync(cancellationToken);
                if (rows == 0)
                {
                    continue;   // 刚被人工复核了
                }

                handled++;
                await audit.WriteAsync(
                    SystemActor, FaceMatchAuditActions.TaskReview, task.StoreCode, task.TaskId,
                    $"auto:confirmed;min={threshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}",
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 日志里只有任务编号与异常类型，不带相似度或门店以外的内容。
                logger.LogWarning(ex, "命中任务自动确认失败，下一轮重试: Task={TaskId}", task.TaskId);
            }
        }

        return handled;
    }

    // ---------- 1. 访客库 ----------

    /// <summary>
    /// 删除已过期的访客库：门店本地营业日结束（本地次日 0 点）或接收端写入的 DeleteAfterUtc，取先到者。
    /// 腾讯云删除失败的留到下一轮；DeleteGroup 对不存在的库是幂等成功。返回本轮删除的库数。
    /// </summary>
    public async Task<int> CleanupVisitGroupsAsync(CancellationToken cancellationToken)
    {
        if (!library.IsAvailable)
        {
            return 0;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var groups = await _db.Queryable<FaceVisitGroup>().Where(g => g.DeletedAtUtc == null).ToListAsync(cancellationToken);
        if (groups.Count == 0)
        {
            return 0;
        }

        var zones = await LoadStoreZonesAsync(groups.Select(g => g.StoreCode).Distinct().ToList(), cancellationToken);
        var deleted = 0;
        foreach (var group in groups)
        {
            var zone = zones.GetValueOrDefault(group.StoreCode) ?? StoreTimeZonePolicy.Sydney;
            var dayEnd = FaceMatchRules.LocalMidnightToUtc(group.BusinessDate.Date.AddDays(1), zone);
            if (FaceMatchRules.AsUtc(group.DeleteAfterUtc) > nowUtc && dayEnd > nowUtc)
            {
                continue;
            }

            try
            {
                await library.DeleteGroupAsync(group.GroupId, cancellationToken);
            }
            catch (FaceLibraryException ex)
            {
                logger.LogWarning("访客库删除未完成，下一轮重试: Group={GroupId}, Kind={Kind}, Code={Code}", group.GroupId, ex.Kind, ex.Code);
                continue;
            }

            await _db.Updateable<FaceVisitGroup>()
                .SetColumns(g => new FaceVisitGroup { DeletedAtUtc = nowUtc })
                .Where(g => g.GroupId == group.GroupId && g.DeletedAtUtc == null)
                .ExecuteCommandAsync(cancellationToken);
            await audit.WriteAsync(SystemActor, FaceMatchAuditActions.VisitGroupDelete, group.StoreCode, group.GroupId, "ok", cancellationToken);
            deleted++;
        }

        return deleted;
    }

    /// <summary>
    /// 扫描腾讯云里遗漏的过期访客库（库表漏记 / 删除后残留）：库名形如 v{门店}_{yyyyMMdd}，
    /// 营业日早于「今天（UTC）」两天以上的一律删除。只认这个命名格式，绝不碰 staff / unwelcome 等其他库。
    /// </summary>
    public async Task<int> SweepOrphanVisitGroupsAsync(CancellationToken cancellationToken)
    {
        if (!library.IsAvailable)
        {
            return 0;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = nowUtc.Date.AddDays(-OrphanGroupMinAgeDays);
        var deleted = 0;
        IReadOnlyList<FaceLibraryGroup> groups;
        try
        {
            groups = await library.ListGroupsAsync(cancellationToken);
        }
        catch (FaceLibraryException ex)
        {
            logger.LogWarning("扫描过期访客库失败，下一轮重试: Kind={Kind}, Code={Code}", ex.Kind, ex.Code);
            return 0;
        }

        var known = groups.Count == 0
            ? new HashSet<string>()
            : (await _db.Queryable<FaceVisitGroup>().Select(g => g.GroupId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var storeCodes = (await _db.Queryable<Store>().Select(s => s.StoreCode).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var match = VisitGroupPattern.Match(group.GroupId);
            if (!match.Success
                || !DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var businessDate)
                || businessDate >= cutoff)
            {
                continue;
            }

            // 命名格式之外再加一道保险：必须是库表里登记过的访客库，或门店编码确实是我们的门店；
            // 同一腾讯云账号下别的系统建的库（哪怕名字碰巧像 v…_yyyyMMdd）不会被动。
            if (!known.Contains(group.GroupId) && !storeCodes.Contains(match.Groups["store"].Value))
            {
                continue;
            }

            try
            {
                await library.DeleteGroupAsync(group.GroupId, cancellationToken);
            }
            catch (FaceLibraryException ex)
            {
                logger.LogWarning("过期访客库删除未完成: Group={GroupId}, Kind={Kind}, Code={Code}", group.GroupId, ex.Kind, ex.Code);
                continue;
            }

            await _db.Updateable<FaceVisitGroup>()
                .SetColumns(g => new FaceVisitGroup { DeletedAtUtc = nowUtc })
                .Where(g => g.GroupId == group.GroupId && g.DeletedAtUtc == null)
                .ExecuteCommandAsync(cancellationToken);
            await audit.WriteAsync(
                SystemActor, FaceMatchAuditActions.VisitGroupDelete, match.Groups["store"].Value, group.GroupId, "orphan", cancellationToken);
            deleted++;
        }

        return deleted;
    }

    // ---------- 2. 员工库完整性 ----------

    /// <summary>
    /// 每日对比腾讯云员工库人数与库里 Status=Active 的登记数。不一致写审计并记 Warning，返回是否一致。
    /// 接收端建访客时持有的子账号理论上能往任何库建人（设计 9.10），这是对员工库被旁路写入的补偿检测。
    /// </summary>
    public async Task<bool?> CheckStaffLibraryIntegrityAsync(CancellationToken cancellationToken)
    {
        if (!library.IsAvailable)
        {
            return null;
        }

        int? remote;
        try
        {
            remote = await library.GetPersonCountAsync(FaceMatchRules.StaffGroupId, cancellationToken);
        }
        catch (FaceLibraryException ex)
        {
            logger.LogWarning("员工库完整性检查失败，下一轮重试: Kind={Kind}, Code={Code}", ex.Kind, ex.Code);
            return null;
        }

        var expected = await _db.Queryable<FaceEnrollment>().CountAsync(e => e.Status == FaceEnrollmentStatus.Active, cancellationToken);
        var actual = remote ?? 0;
        if (actual == expected)
        {
            return true;
        }

        logger.LogWarning("员工库人数与有效登记数不一致: Library={Library}, Enrollments={Enrollments}", actual, expected);
        await audit.WriteAsync(
            SystemActor, FaceMatchAuditActions.EnrollmentIntegrity, null, $"library={actual};db={expected}", "mismatch", cancellationToken);
        return false;
    }

    // ---------- 3. Capture 记录保留清理 ----------

    /// <summary>
    /// 删除超过保留天数（FaceMatch:CaptureRetentionDays，默认 7）的抓拍事件与图片行（图片对象由接收端 / COS 生命周期清理）。
    /// 分批删除，每轮最多约 1 万行，避免长事务；没有任何门店启用时直接空转。返回删除的事件行数。
    /// </summary>
    public async Task<int> CleanupCaptureRecordsAsync(CancellationToken cancellationToken)
    {
        if (!await AnyStoreEnabledAsync(cancellationToken))
        {
            return 0;
        }

        var days = settingsProvider.Options.CaptureRetentionDays;
        if (days < 1)
        {
            days = 7;
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
        var total = 0;
        for (var batch = 0; batch < RetentionMaxBatches; batch++)
        {
            var ids = await _db.Queryable<FaceCaptureEvent>()
                .Where(e => e.ReceivedAtUtc < cutoff)
                .OrderBy(e => e.ReceivedAtUtc)
                .Take(RetentionBatchSize)
                .Select(e => e.CaptureId)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            // 图片行在 SQL Server 上随外键级联删除；显式先删一遍，不依赖级联（也兼容没有外键的环境）。
            var parameters = ids.Select((id, index) => new SugarParameter($"@p{index}", id)).ToList();
            var placeholders = string.Join(",", parameters.Select(p => p.ParameterName));
            await _db.Ado.ExecuteCommandAsync($"DELETE FROM FaceCaptureImage WHERE CaptureId IN ({placeholders})", parameters);
            await _db.Ado.ExecuteCommandAsync($"DELETE FROM FaceCaptureEvent WHERE CaptureId IN ({placeholders})", parameters);
            total += ids.Count;
            if (ids.Count < RetentionBatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            await audit.WriteAsync(SystemActor, FaceMatchAuditActions.CaptureRetention, null, null, $"deleted={total}", cancellationToken);
        }

        return total;
    }

    // ---------- 4. 名单条目 ----------

    /// <summary>
    /// 到期条目自动停用（Pending / Active → Expired，接收端立即停止匹配）并清理腾讯云人员与参考照片；
    /// 另重试 Withdrawn / Expired 但没清理干净的条目。返回本轮处理（停用或清理）的条目数。
    /// </summary>
    public async Task<int> ProcessUnwelcomeEntriesAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var expiredStatus = FaceUnwelcomeEntryStatus.Expired;
        var pendingStatus = FaceUnwelcomeEntryStatus.Pending;
        var activeStatus = FaceUnwelcomeEntryStatus.Active;
        var processed = 0;

        var due = await _db.Queryable<FaceUnwelcomeEntry>()
            .Where(e => (e.Status == pendingStatus || e.Status == activeStatus) && e.ExpiresAtUtc <= nowUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var entry in due)
        {
            var rows = await _db.Updateable<FaceUnwelcomeEntry>()
                .SetColumns(e => new FaceUnwelcomeEntry { Status = expiredStatus })
                .Where(e => e.EntryId == entry.EntryId && (e.Status == pendingStatus || e.Status == activeStatus))
                .ExecuteCommandAsync(cancellationToken);
            if (rows > 0)
            {
                entry.Status = FaceUnwelcomeEntryStatus.Expired;
                await audit.WriteAsync(SystemActor, FaceMatchAuditActions.EntryExpire, null, entry.EntryId, "ok", cancellationToken);
                processed++;
            }
        }

        // 失效但没清理干净的条目（含上面刚到期的）：DeletePerson + 删参考照片，照片键清空即完成。
        var withdrawnStatus = FaceUnwelcomeEntryStatus.Withdrawn;
        var cleanupRows = await _db.Queryable<FaceUnwelcomeEntry>()
            .Where(e => (e.Status == expiredStatus || e.Status == withdrawnStatus) && e.PhotoObjectKeysJson != "[]")
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var entry in cleanupRows)
        {
            if (await unwelcomeService.CleanupEntryAsync(entry, cancellationToken))
            {
                processed++;
            }
        }

        return processed;
    }

    /// <summary>
    /// 到期前 30 天提醒复核（ReviewDueAtUtc 已到、尚未到期的 Active 条目），经通知平台发给提交人与批准人。
    /// DedupeKey = unwelcome-expiry:{EntryId}，重复发布自动去重；发布在业务事务之外、失败只记日志下轮重试。
    /// 文案不含姓名与事件细节。返回本轮新发出的提醒数。
    /// </summary>
    public async Task<int> PublishExpiryRemindersAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var activeStatus = FaceUnwelcomeEntryStatus.Active;
        var entries = await _db.Queryable<FaceUnwelcomeEntry>()
            .Where(e => e.Status == activeStatus && e.ReviewDueAtUtc <= nowUtc && e.ExpiresAtUtc > nowUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var entry in entries)
        {
            var days = Math.Max(0, (int)Math.Ceiling((entry.ExpiresAtUtc - nowUtc).TotalDays));
            var recipients = new[] { entry.SubmittedBy, entry.ApprovedBy, entry.SecondApprovedBy }
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            try
            {
                var result = await notificationPublisher.PublishAsync(new NotificationPublishRequest
                {
                    Type = AppNotificationTypes.FaceUnwelcomeExpiring,
                    Title = "不受欢迎人员名单条目即将到期",
                    Body = $"有名单条目将在 {days} 天内到期，请复核是否需要续期或撤回。",
                    Audience = NotificationAudience.ForUsers(recipients),
                    Data = new Dictionary<string, string> { ["entryId"] = entry.EntryId },
                    DedupeKey = $"unwelcome-expiry:{entry.EntryId}",
                    ExpiresAtUtc = entry.ExpiresAtUtc,
                    CreatedBy = "system:face-match",
                }, cancellationToken);
                if (!result.Deduplicated && result.NotificationId != null)
                {
                    sent++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "名单到期提醒发布失败，下一轮重试: Entry={EntryId}", entry.EntryId);
            }
        }

        return sent;
    }

    // ---------- 5. 命中图到期删除 ----------

    /// <summary>
    /// 删除到期（ImageDeleteAtUtc ≤ 现在，含已驳回提前到期的）命中图，并把 ImageObjectKey 置空。
    /// 只删形态合法且不在管理端三个私有前缀下的键，绝不会借命中任务删到登记照 / 同意书 / 参考照片。
    /// </summary>
    public async Task<int> DeleteExpiredTaskImagesAsync(CancellationToken cancellationToken)
    {
        if (!objectStore.IsConfigured)
        {
            return 0;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var tasks = await _db.Queryable<FaceMatchTask>()
            .Where(t => t.ImageDeleteAtUtc <= nowUtc && t.ImageObjectKey != "")
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var deleted = 0;
        foreach (var task in tasks)
        {
            if (!FaceMatchObjectKeys.IsAllowedHitImageKey(task.ImageObjectKey))
            {
                logger.LogWarning("命中任务的图片键形态不合法，已跳过删除: Task={TaskId}", task.TaskId);
                continue;
            }

            try
            {
                await objectStore.DeleteAsync(task.ImageObjectKey, cancellationToken);
            }
            catch (FaceMatchStoreException ex)
            {
                logger.LogWarning("命中图删除失败，下一轮重试: Task={TaskId}, Kind={Kind}", task.TaskId, ex.Kind);
                continue;
            }

            await _db.Updateable<FaceMatchTask>()
                .SetColumns(t => new FaceMatchTask { ImageObjectKey = "" })
                .Where(t => t.TaskId == task.TaskId)
                .ExecuteCommandAsync(cancellationToken);
            await audit.WriteAsync(SystemActor, FaceMatchAuditActions.TaskImageDelete, task.StoreCode, task.TaskId, "ok", cancellationToken);
            deleted++;
        }

        return deleted;
    }

    // ---------- 6. 撤回补偿 ----------

    /// <summary>
    /// 同意已撤回但腾讯云删除未完成的登记（重新尝试 DeletePerson + 删登记照）；
    /// 以及已撤回登记留在私有桶里没删掉的登记照。返回本轮补偿成功的登记数。
    /// </summary>
    public async Task<int> RetryEnrollmentDeletionsAsync(CancellationToken cancellationToken)
    {
        var activeStatus = FaceEnrollmentStatus.Active;
        var withdrawnStatus = FaceEnrollmentStatus.Withdrawn;
        var done = 0;

        var pending = await _db.Queryable<FaceEnrollment>()
            .InnerJoin<FaceConsentRecord>((e, c) => e.ConsentId == c.ConsentId)
            .Where((e, c) => e.Status == activeStatus && c.WithdrawnAtUtc != null)
            .Select((e, c) => e)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var enrollment in pending)
        {
            if (await staffService.TryWithdrawEnrollmentAsync(enrollment, cancellationToken))
            {
                done++;
            }
        }

        var leftovers = await _db.Queryable<FaceEnrollment>()
            .Where(e => e.Status == withdrawnStatus && e.PhotoObjectKeysJson != "[]")
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var enrollment in leftovers)
        {
            var keys = FaceMatchRules.ParseKeys(enrollment.PhotoObjectKeysJson);
            if (keys.Count > 0 && objectStore.IsConfigured && await staffService.TryDeleteObjectsAsync(keys, cancellationToken))
            {
                enrollment.PhotoObjectKeysJson = "[]";
                await _db.Updateable(enrollment).UpdateColumns(e => new { e.PhotoObjectKeysJson }).ExecuteCommandAsync(cancellationToken);
                done++;
            }
        }

        return done;
    }

    // ---------- 7. 地域改动后作废跨境披露审查 ----------

    /// <summary>
    /// 当前配置的处理地域有效、而门店档案里的 CrossBorderRegion 与之不同：审查记录作废（清空地域与审查字段）并写审计。
    /// 2026-10-13 用户决定 Match 的合规闸门改为警告：<b>不再强制关闭任何档案</b>（Mode / 员工排除 / 去重 / 名单开关保持原样）；
    /// 审查记录被清空后，Web 档案页与门店列表会把该店的跨境披露审查显示为未补齐的警告，须重新审查并补录。
    /// 配置地域无效 / 功能未开启时什么都不动（配置写错不应波及门店档案）。返回被作废的门店数。
    /// </summary>
    public async Task<int> InvalidateStaleCrossBorderReviewsAsync(CancellationToken cancellationToken)
    {
        var resolved = settingsProvider.Resolved;
        if (!resolved.Enabled || resolved.Region == null)
        {
            return 0;
        }

        var region = resolved.Region;
        var stale = await _db.Queryable<FaceStoreProfile>()
            .Where(p => p.CrossBorderRegion != null && p.CrossBorderRegion != region)
            .ToListAsync(cancellationToken);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var profile in stale)
        {
            var oldRegion = profile.CrossBorderRegion;
            // 只清空审查记录，不碰 Mode 与各开关：缺的审查记录由闸门计算为警告（不阻塞），而不是把门店关掉。
            profile.CrossBorderRegion = null;
            profile.CrossBorderReviewRef = null;
            profile.CrossBorderReviewedBy = null;
            profile.CrossBorderReviewedAt = null;
            profile.UpdatedAtUtc = nowUtc;
            profile.UpdatedBy = FaceMatchAuditActions.SystemActorId;
            await _db.Updateable(profile).ExecuteCommandAsync(cancellationToken);
            logger.LogWarning(
                "配置地域已改动，门店跨境披露审查作废（不再强制关闭档案，该店将显示合规记录警告）: Store={StoreCode}, From={From}, To={To}, Mode={Mode}",
                profile.StoreCode, oldRegion, region, profile.Mode);
            // 审计 Result 只有 32 位：固定写 review-cleared（只作废审查记录，不改任何开关）。
            await audit.WriteAsync(
                SystemActor, FaceMatchAuditActions.ProfileInvalidated, profile.StoreCode, profile.StoreCode,
                "review-cleared", cancellationToken);
        }

        return stale.Count;
    }

    // ---------- 辅助 ----------

    private async Task<Dictionary<string, string>> LoadStoreZonesAsync(IReadOnlyList<string> storeCodes, CancellationToken cancellationToken)
    {
        var stores = await _db.Queryable<Store>()
            .Where(s => storeCodes.Contains(s.StoreCode) && !s.IsDeleted)
            .ToListAsync(cancellationToken);
        var profiles = (await _db.Queryable<FaceStoreProfile>().Where(p => storeCodes.Contains(p.StoreCode)).ToListAsync(cancellationToken))
            .ToDictionary(p => p.StoreCode, p => p.TimeZoneId);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var store in stores)
        {
            result[store.StoreCode] = FaceMatchRules.ResolveTimeZoneId(profiles.GetValueOrDefault(store.StoreCode), store.TimeZoneId, store.Address);
        }

        return result;
    }
}

/// <summary>
/// 命中提示发布：轮询 NotifiedAtUtc IS NULL 的新任务，经通知平台发给该店持有 FaceMatch.Unwelcome.Review 的人。
/// 1. 发布在业务事务之外：通知发布器内部自带 UseTranAsync（N1），绝不能放进外层事务；
/// 2. 每条任务单独 try/catch，失败只记日志（不含内容）、下轮重试，不阻塞其他任务；
/// 3. DedupeKey = unwelcome-hit:{TaskId}（撤回后同键不可再发，所以这里从不撤回）；
/// 4. 文案固定且只用英文（用户 2026-10 的通知文案规则）："Possible match (similarity NN%). The score is not the probability that this is the same person; a reviewer must check it."，不含姓名与事件细节；
/// 5. 没有接收人的任务在命中图到期前继续重试（也许稍后有人被授权）；已复核结束或图片已到期的任务直接标记已处理。
/// </summary>
public sealed class FaceHitNotifier(
    SqlSugarContext context,
    INotificationPublisher notificationPublisher,
    TimeProvider timeProvider,
    ILogger<FaceHitNotifier> logger)
{
    public const int BatchLimit = 50;

    private readonly ISqlSugarClient _db = context.Db;

    /// <summary>发布一批待提示的命中任务；返回本轮标记为已处理的任务数。</summary>
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var tasks = await _db.Queryable<FaceMatchTask>()
            .Where(t => t.NotifiedAtUtc == null)
            .OrderBy(t => t.CapturedAtUtc)
            .Take(BatchLimit)
            .ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return 0;
        }

        var handled = 0;
        foreach (var task in tasks)
        {
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var closed = task.ReviewStatus != FaceMatchReviewStatus.Pending || task.ImageDeleteAtUtc <= nowUtc;
            if (!closed)
            {
                try
                {
                    var percent = (int)Math.Clamp(Math.Round(task.Score, MidpointRounding.AwayFromZero), 0, 100);
                    var result = await notificationPublisher.PublishAsync(new NotificationPublishRequest
                    {
                        Type = AppNotificationTypes.FaceUnwelcomeHit,
                        Title = "Possible match alert",
                        Body = $"Possible match (similarity {percent}%). The score is not the probability that this is the same person; a reviewer must check it.",
                        Audience = NotificationAudience.ForStore(task.StoreCode, Permissions.FaceMatch.Unwelcome.Review),
                        Priority = AppNotificationPriorities.High,
                        StoreCode = task.StoreCode,
                        Data = new Dictionary<string, string> { ["taskId"] = task.TaskId, ["storeCode"] = task.StoreCode },
                        DedupeKey = $"unwelcome-hit:{task.TaskId}",
                        ExpiresAtUtc = task.ImageDeleteAtUtc,
                        CreatedBy = "system:face-match",
                    }, cancellationToken);

                    if (result.NotificationId == null && !result.Deduplicated)
                    {
                        // 没有接收人：命中图到期前继续重试，不标记已处理。
                        logger.LogInformation("命中提示暂无接收人，稍后重试: Task={TaskId}", task.TaskId);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 日志里只有任务编号与异常类型，不带相似度或门店以外的内容。
                    logger.LogWarning(ex, "命中提示发布失败，下一轮重试: Task={TaskId}", task.TaskId);
                    continue;
                }
            }

            var rows = await _db.Updateable<FaceMatchTask>()
                .SetColumns(t => new FaceMatchTask { NotifiedAtUtc = nowUtc })
                .Where(t => t.TaskId == task.TaskId && t.NotifiedAtUtc == null)
                .ExecuteCommandAsync(cancellationToken);
            if (rows > 0)
            {
                handled++;
            }
        }

        return handled;
    }
}
