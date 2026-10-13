using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 不受欢迎人员名单（原「黑名单」，设计第 14 节）。硬条件全部在这里强制：
/// 1. 事件类别是枚举 ViolenceOrThreat / OrganisedOrRepeatTheft / Other，没有「一般偷窃」；Other 必须有说明，非管理员需要两位不同的批准人；
/// 2. 证据引用必填、到期日必填且上限 12 个月；
/// 3. 非管理员：提交人 ≠ 批准人；<b>管理员一个人批准即可生效（2026-10-13 用户决定）</b>：可以批准自己提交的条目，
///    Other 类别也只需一位管理员批准，自批 / 单人批准在审计里留痕（Result 为 active;self-approved / active;admin-solo）。
///    批准后才在腾讯云建人，且至少一个生效门店已开启名单（UnwelcomeListEnabled，只看这个开关；
///    专项评估 / 律师确认等记录缺口在门店档案里是警告，不在这里拦截）——开关没开之前，照片只留在私有桶里，不会发往腾讯云；
/// 4. 撤回 / 到期：先把状态改成 Withdrawn / Expired（接收端只用 Active 条目，立即停止匹配），再 DeletePerson 并删参考照片，
///    删不干净的由后台任务重试（照片键清空即清理完成的标志）；
/// 5. 复核一个人就够（2026-10-13 用户决定）：任何有复核权限的人判断一次即结案——「是同一个人」→ Confirmed，「不是」→ Dismissed，
///    Dismissed 后命中图提前到期删除；旧版遗留的「已一审、待二审」任务由下一位复核人（含第一复核人本人）直接结案；
/// 6. 命中图与参考照片只经服务端转发查看（对象键只来自数据库、归属校验、只给 Review 权限），每次查看都写持久审计。
/// 全部响应不含姓名与事件细节。
/// </summary>
public sealed class FaceUnwelcomeService(
    SqlSugarContext context,
    IFaceLibrary library,
    IFaceMatchObjectStore objectStore,
    IFaceMatchSettingsProvider settingsProvider,
    IFaceMatchAudit audit,
    TimeProvider timeProvider,
    ILogger<FaceUnwelcomeService> logger
) : IFaceUnwelcomeService
{
    public const int MaxPageSize = 100;
    private const int MaxEvidenceRefLength = 100;
    private const int MaxCategoryNoteLength = 200;
    private const int MaxDisputeNoteLength = 500;
    private const int MaxReasonLength = 200;
    private const int StatsMinReviewedForFlag = 5;
    private const decimal StatsDismissRateForFlag = 0.8m;

    private readonly ISqlSugarClient _db = context.Db;

    // ---------- 条目 ----------

    public async Task<FaceMatchPageDto<FaceUnwelcomeEntryDto>> ListEntriesAsync(
        string? status, string? storeCode, int page, int pageSize, FaceMatchActor actor, bool canApprove,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, MaxPageSize);
        var number = Math.Max(1, page);
        var statusValue = ParseEntryStatus(status);
        var statusFilter = statusValue.GetValueOrDefault();
        var code = string.IsNullOrWhiteSpace(storeCode) ? null : storeCode.Trim();
        var allowedStores = await GetAllowedStoresAsync(actor, cancellationToken);

        var rows = await _db.Queryable<FaceUnwelcomeEntry>()
            .WhereIF(statusValue.HasValue, e => e.Status == statusFilter)
            .OrderBy(e => e.SubmittedAtUtc, OrderByType.Desc)
            .ToListAsync(cancellationToken);

        // 门店范围在内存里过滤（条目总量很小）：管理员不限；其他人只能看到与自己门店有交集的条目。
        var visible = rows
            .Where(e => code == null || FaceMatchRules.SplitStoreScope(e.StoreScope).Contains(code, StringComparer.Ordinal))
            .Where(e => allowedStores == null || FaceMatchRules.SplitStoreScope(e.StoreScope).Any(allowedStores.Contains))
            .ToList();

        var pageRows = visible.Skip((number - 1) * size).Take(size).ToList();
        var names = await FaceMatchErrors.LoadUserNamesAsync(
            _db,
            pageRows.SelectMany(e => new[] { e.SubmittedBy, e.ApprovedBy, e.SecondApprovedBy }),
            cancellationToken);
        return new FaceMatchPageDto<FaceUnwelcomeEntryDto>
        {
            Items = pageRows.Select(e => ToDto(e, names, actor, canApprove)).ToList(),
            Page = number,
            PageSize = size,
            HasMore = visible.Count > number * size,
        };
    }

    public async Task<FaceUnwelcomeEntryDto> CreateEntryAsync(
        FaceUnwelcomeEntryCreateRequest request, IReadOnlyList<FaceMatchUpload> photos, FaceMatchActor actor,
        CancellationToken cancellationToken = default)
    {
        if (request.IncidentCategory is not int category || !FaceIncidentCategory.IsValid(category))
        {
            throw FaceMatchErrors.Invalid("incidentCategory", "事件类别只能是暴力或威胁、有组织或反复盗窃、其他。");
        }

        if (!FaceMatchRules.TryNormalizeText(request.CategoryNote, MaxCategoryNoteLength, out var categoryNote))
        {
            throw FaceMatchErrors.Invalid("categoryNote", "类别说明过长或含非法字符（最长 200）。");
        }

        if (category == FaceIncidentCategory.Other && categoryNote == null)
        {
            throw FaceMatchErrors.Invalid("categoryNote", "「其他」类别必须填写说明，并需要两位不同的批准人。");
        }

        if (!FaceMatchRules.TryNormalizeText(request.EvidenceRef, MaxEvidenceRefLength, out var evidenceRef) || evidenceRef == null)
        {
            throw FaceMatchErrors.Invalid("evidenceRef", "证据引用必填（事件记录编号或警方报告编号，最长 100，不要填个人隐私细节）。");
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (request.ExpiresAtUtc is not DateTime expiresRaw)
        {
            throw FaceMatchErrors.Invalid("expiresAtUtc", "到期日必填。");
        }

        var expiresAtUtc = DateTime.SpecifyKind(expiresRaw.ToUniversalTime(), DateTimeKind.Utc);
        if (expiresAtUtc <= nowUtc)
        {
            throw FaceMatchErrors.Invalid("expiresAtUtc", "到期日必须晚于当前时间。");
        }

        if (expiresAtUtc > nowUtc.AddMonths(FaceMatchRules.MaxUnwelcomeLifetimeMonths))
        {
            throw FaceMatchErrors.Invalid("expiresAtUtc", $"到期日不能超过 {FaceMatchRules.MaxUnwelcomeLifetimeMonths} 个月。");
        }

        var scope = await NormalizeStoreScopeAsync(request.StoreScope, actor, cancellationToken);

        if (photos.Count is < 1 or > FaceMatchRules.MaxUnwelcomePhotos)
        {
            throw FaceMatchErrors.Invalid("photos", $"参考照片需要 1–{FaceMatchRules.MaxUnwelcomePhotos} 张。");
        }

        var detected = new List<DetectedImage>();
        foreach (var photo in photos)
        {
            if (photo.Bytes.Length == 0 || photo.Bytes.Length > FaceMatchRules.MaxImageBytes)
            {
                throw FaceMatchErrors.Invalid("photos", "参考照片为空或单张超过 5MB。");
            }

            detected.Add(FaceMatchRules.DetectImage(photo.Bytes)
                ?? throw FaceMatchErrors.Invalid("photos", "参考照片只支持 JPG 或 PNG。"));
        }

        if (!objectStore.IsConfigured)
        {
            throw FaceMatchErrors.StoreNotConfigured();
        }

        var entryId = FaceMatchRules.NewId();
        var keys = detected.Select((d, i) => FaceMatchObjectKeys.Unwelcome(entryId, i, d.Extension)).ToList();
        var uploaded = new List<string>();
        try
        {
            for (var i = 0; i < photos.Count; i++)
            {
                await objectStore.PutAsync(keys[i], photos[i].Bytes, detected[i].ContentType, cancellationToken);
                uploaded.Add(keys[i]);
            }

            var entry = new FaceUnwelcomeEntry
            {
                EntryId = entryId,
                TencentPersonId = FaceMatchRules.UnwelcomePersonId(entryId),
                IncidentCategory = (byte)category,
                EvidenceRef = evidenceRef,
                StoreScope = FaceMatchRules.JoinStoreScope(scope),
                SubmittedBy = actor.Id,
                SubmittedAtUtc = nowUtc,
                ReviewDueAtUtc = ReviewDue(expiresAtUtc, nowUtc),
                ExpiresAtUtc = expiresAtUtc,
                Status = FaceUnwelcomeEntryStatus.Pending,
                PhotoObjectKeysJson = FaceMatchRules.SerializeKeys(keys),
                CategoryNote = categoryNote,
            };
            await _db.Insertable(entry).ExecuteCommandAsync(cancellationToken);
            await audit.WriteAsync(actor, FaceMatchAuditActions.EntrySubmit, null, entryId, $"category={category}", cancellationToken);

            var names = await FaceMatchErrors.LoadUserNamesAsync(_db, [entry.SubmittedBy], cancellationToken);
            return ToDto(entry, names, actor, canApprove: false);
        }
        catch (FaceMatchStoreException ex)
        {
            await TryDeleteObjectsAsync(uploaded, CancellationToken.None);
            throw FaceMatchErrors.FromStore(ex);
        }
        catch
        {
            await TryDeleteObjectsAsync(uploaded, CancellationToken.None);
            throw;
        }
    }

    public async Task<FaceUnwelcomeEntryDto> ApproveEntryAsync(
        string entryId, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var entry = await RequireEntryAsync(entryId, actor, cancellationToken);
        if (entry.Status != FaceUnwelcomeEntryStatus.Pending)
        {
            throw new FaceMatchException(409, "FACE_MATCH_ENTRY_NOT_PENDING", "该条目不是待批准状态。");
        }

        // 管理员一个人批准即可生效：不受「提交人 ≠ 批准人」和「Other 需要两位」的限制；非管理员规则不变。
        var adminSolo = actor.IsAdmin;
        var selfApproval = string.Equals(entry.SubmittedBy, actor.Id, StringComparison.OrdinalIgnoreCase);
        if (selfApproval && !adminSolo)
        {
            throw new FaceMatchException(409, "FACE_MATCH_APPROVER_IS_SUBMITTER", "批准人不能是提交人本人（管理员除外）。");
        }

        if (!adminSolo && string.Equals(entry.ApprovedBy, actor.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new FaceMatchException(409, "FACE_MATCH_ALREADY_APPROVED_BY_YOU", "你已经批准过该条目，「其他」类别需要另一位批准人。");
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (entry.ExpiresAtUtc <= nowUtc)
        {
            throw new FaceMatchException(409, "FACE_MATCH_ENTRY_EXPIRED", "该条目已过到期日，不能再批准。");
        }

        var policyNeeded = ApprovalsNeeded(entry);
        var needed = adminSolo ? 1 : policyNeeded;
        var done = ApprovalsDone(entry);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (done + 1 < needed)
        {
            // 「其他」类别：第一位批准人只记录，不入名单；第二位不同的批准人才会生效。
            var actorId = actor.Id;
            var pendingStatus = FaceUnwelcomeEntryStatus.Pending;
            var rows = await _db.Updateable<FaceUnwelcomeEntry>()
                .SetColumns(e => new FaceUnwelcomeEntry { ApprovedBy = actorId, ApprovedAtUtc = nowUtc })
                .Where(e => e.EntryId == entry.EntryId && e.Status == pendingStatus && e.ApprovedBy == null)
                .ExecuteCommandAsync(cancellationToken);
            if (rows == 0)
            {
                throw new FaceMatchException(409, "FACE_MATCH_ENTRY_CHANGED", "条目刚刚被其他人修改，请刷新后重试。");
            }

            entry.ApprovedBy = actor.Id;
            entry.ApprovedAtUtc = nowUtc;
            await audit.WriteAsync(actor, FaceMatchAuditActions.EntryApprove, null, entry.EntryId, "first-of-two", cancellationToken);
        }
        else
        {
            await ActivateAsync(entry, actor, nowUtc, cancellationToken);
            // 审计留痕：自批、管理员单人批准 Other 类别（本应两位）都要看得出来。Result 列最长 32 位。
            var approvalResult = selfApproval
                ? "active;self-approved"
                : adminSolo && policyNeeded > 1 && done == 0 ? "active;admin-solo" : "active";
            await audit.WriteAsync(actor, FaceMatchAuditActions.EntryApprove, null, entry.EntryId, approvalResult, cancellationToken);
        }

        await FillNamesAsync(names, [entry], cancellationToken);
        return ToDto(entry, names, actor, canApprove: false);
    }

    /// <summary>
    /// 最后一位批准人通过：读私有桶里的参考照片 → 腾讯云建人 → 条目改 Active。
    /// 没有任何生效门店开启名单（UnwelcomeListEnabled）时拒绝（开关没开就不把照片发往境外）；DB 更新失败或被并发改动时回滚腾讯云人员。
    /// </summary>
    private async Task ActivateAsync(FaceUnwelcomeEntry entry, FaceMatchActor actor, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var settings = settingsProvider.Resolved;
        if (!settings.Enabled || !library.IsAvailable)
        {
            throw FaceMatchErrors.Disabled(settings.DisabledReason);
        }

        if (!objectStore.IsConfigured)
        {
            throw FaceMatchErrors.StoreNotConfigured();
        }

        var scope = FaceMatchRules.SplitStoreScope(entry.StoreScope);
        var enabledStores = await _db.Queryable<FaceStoreProfile>()
            .Where(p => scope.Contains(p.StoreCode) && p.UnwelcomeListEnabled)
            .AnyAsync(cancellationToken);
        if (!enabledStores)
        {
            throw new FaceMatchException(
                409, "FACE_MATCH_UNWELCOME_NOT_ENABLED",
                "生效门店都还没有开启「不受欢迎人员名单」，不能批准入名单；请先在门店档案里开启。");
        }

        var keys = FaceMatchRules.ParseKeys(entry.PhotoObjectKeysJson);
        if (keys.Count == 0)
        {
            throw FaceMatchErrors.ImageGone();
        }

        var personCreated = false;
        try
        {
            var images = new List<byte[]>();
            foreach (var key in keys)
            {
                images.Add(await objectStore.ReadAsync(key, FaceMatchRules.MaxImageBytes, cancellationToken));
            }

            await library.EnsureGroupAsync(FaceMatchRules.UnwelcomeGroupId, FaceMatchRules.UnwelcomeGroupId, cancellationToken);
            await library.CreatePersonAsync(
                FaceMatchRules.UnwelcomeGroupId, entry.TencentPersonId, entry.EntryId, images[0], cancellationToken);
            personCreated = true;
            if (images.Count > 1)
            {
                var created = await library.CreateFacesAsync(entry.TencentPersonId, images.Skip(1).ToList(), cancellationToken);
                if (created.Count < images.Count - 1)
                {
                    throw new FaceLibraryException(
                        FaceLibraryFailureKind.InvalidImage, "PartialFaces", "部分参考照片没有通过腾讯云的人脸检测。");
                }
            }

            var isSecond = entry.ApprovedBy != null;
            // 表达式里只放局部变量：SqlSugar 会把方法调用 / 三元表达式翻译成 SQL。
            var reviewDue = ReviewDue(entry.ExpiresAtUtc, nowUtc);
            var firstApprover = entry.ApprovedBy;
            var activeStatus = FaceUnwelcomeEntryStatus.Active;
            var pendingStatus = FaceUnwelcomeEntryStatus.Pending;
            var actorId = actor.Id;
            var rows = isSecond
                ? await _db.Updateable<FaceUnwelcomeEntry>()
                    .SetColumns(e => new FaceUnwelcomeEntry
                    {
                        Status = activeStatus,
                        SecondApprovedBy = actorId,
                        SecondApprovedAtUtc = nowUtc,
                        ReviewDueAtUtc = reviewDue,
                    })
                    .Where(e => e.EntryId == entry.EntryId && e.Status == pendingStatus && e.ApprovedBy == firstApprover)
                    .ExecuteCommandAsync(cancellationToken)
                : await _db.Updateable<FaceUnwelcomeEntry>()
                    .SetColumns(e => new FaceUnwelcomeEntry
                    {
                        Status = activeStatus,
                        ApprovedBy = actorId,
                        ApprovedAtUtc = nowUtc,
                        ReviewDueAtUtc = reviewDue,
                    })
                    .Where(e => e.EntryId == entry.EntryId && e.Status == pendingStatus && e.ApprovedBy == null)
                    .ExecuteCommandAsync(cancellationToken);
            if (rows == 0)
            {
                throw new FaceMatchException(409, "FACE_MATCH_ENTRY_CHANGED", "条目刚刚被其他人修改，请刷新后重试。");
            }

            entry.Status = FaceUnwelcomeEntryStatus.Active;
            if (isSecond)
            {
                entry.SecondApprovedBy = actor.Id;
                entry.SecondApprovedAtUtc = nowUtc;
            }
            else
            {
                entry.ApprovedBy = actor.Id;
                entry.ApprovedAtUtc = nowUtc;
            }

            entry.ReviewDueAtUtc = ReviewDue(entry.ExpiresAtUtc, nowUtc);
        }
        catch (Exception ex) when (ex is FaceLibraryException or FaceMatchStoreException or FaceMatchException)
        {
            if (personCreated)
            {
                await TryDeletePersonAsync(entry.TencentPersonId);
            }

            throw ex switch
            {
                FaceLibraryException library => FaceMatchErrors.FromLibrary(library),
                FaceMatchStoreException store => FaceMatchErrors.FromStore(store),
                _ => ex,
            };
        }
    }

    public async Task<FaceUnwelcomeEntryDto> WithdrawEntryAsync(
        string entryId, FaceUnwelcomeWithdrawRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        if (!FaceMatchRules.TryNormalizeText(request.Reason, MaxReasonLength, out _))
        {
            throw FaceMatchErrors.Invalid("reason", "撤回原因过长或含非法字符（最长 200）。");
        }

        var entry = await RequireEntryAsync(entryId, actor, cancellationToken);
        if (entry.Status is FaceUnwelcomeEntryStatus.Pending or FaceUnwelcomeEntryStatus.Active)
        {
            // 先把状态改掉（接收端只用 Active 条目，立刻停止匹配），再清理腾讯云人员与参考照片。
            var withdrawnStatus = FaceUnwelcomeEntryStatus.Withdrawn;
            var pendingStatus = FaceUnwelcomeEntryStatus.Pending;
            var activeStatus = FaceUnwelcomeEntryStatus.Active;
            var rows = await _db.Updateable<FaceUnwelcomeEntry>()
                .SetColumns(e => new FaceUnwelcomeEntry { Status = withdrawnStatus })
                .Where(e => e.EntryId == entry.EntryId && (e.Status == pendingStatus || e.Status == activeStatus))
                .ExecuteCommandAsync(cancellationToken);
            if (rows > 0)
            {
                entry.Status = FaceUnwelcomeEntryStatus.Withdrawn;
                await audit.WriteAsync(actor, FaceMatchAuditActions.EntryWithdraw, null, entry.EntryId, "ok", cancellationToken);
                await CleanupEntryAsync(entry, CancellationToken.None);
            }
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await FillNamesAsync(names, [entry], cancellationToken);
        return ToDto(entry, names, actor, canApprove: false);
    }

    public async Task<FaceUnwelcomeEntryDto> SetDisputeNoteAsync(
        string entryId, FaceUnwelcomeDisputeRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        if (!FaceMatchRules.TryNormalizeText(request.Note, MaxDisputeNoteLength, out var note))
        {
            throw FaceMatchErrors.Invalid("note", "申诉 / 更正说明过长或含非法字符（最长 500）。");
        }

        var entry = await RequireEntryAsync(entryId, actor, cancellationToken);
        entry.DisputeNote = note;
        await _db.Updateable(entry).UpdateColumns(e => new { e.DisputeNote }).ExecuteCommandAsync(cancellationToken);
        await audit.WriteAsync(actor, FaceMatchAuditActions.EntryDispute, null, entry.EntryId, "ok", cancellationToken);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await FillNamesAsync(names, [entry], cancellationToken);
        return ToDto(entry, names, actor, canApprove: false);
    }

    /// <summary>
    /// 清理一条已失效（Withdrawn / Expired）条目：DeletePerson（不存在视为成功）→ 删参考照片 → 照片键清空。
    /// 照片键清空是「清理完成」的标志，没清完的后台任务会继续重试。返回 true 表示已清理干净。
    /// </summary>
    internal async Task<bool> CleanupEntryAsync(FaceUnwelcomeEntry entry, CancellationToken cancellationToken)
    {
        var keys = FaceMatchRules.ParseKeys(entry.PhotoObjectKeysJson);
        if (keys.Count == 0)
        {
            return true;
        }

        if (!library.IsAvailable)
        {
            return false;
        }

        try
        {
            await library.DeletePersonAsync(entry.TencentPersonId, cancellationToken);
        }
        catch (FaceLibraryException ex)
        {
            logger.LogWarning("名单条目清理时腾讯云删除未完成: Entry={EntryId}, Kind={Kind}, Code={Code}", entry.EntryId, ex.Kind, ex.Code);
            return false;
        }

        if (!await TryDeleteObjectsAsync(keys, cancellationToken))
        {
            return false;
        }

        entry.PhotoObjectKeysJson = "[]";
        await _db.Updateable(entry).UpdateColumns(e => new { e.PhotoObjectKeysJson }).ExecuteCommandAsync(cancellationToken);
        return true;
    }

    // ---------- 复核任务 ----------

    public async Task<FaceMatchPageDto<FaceUnwelcomeTaskDto>> ListTasksAsync(
        string? status, string? storeCode, int page, int pageSize, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, MaxPageSize);
        var number = Math.Max(1, page);
        var statusValue = ParseReviewStatus(status);
        var statusFilter = statusValue.GetValueOrDefault();
        var code = string.IsNullOrWhiteSpace(storeCode) ? null : storeCode.Trim();
        var allowedStores = await GetAllowedStoresAsync(actor, cancellationToken);
        var allowedList = allowedStores?.ToList() ?? [];

        var rows = await _db.Queryable<FaceMatchTask>()
            .WhereIF(statusValue.HasValue, t => t.ReviewStatus == statusFilter)
            .WhereIF(code != null, t => t.StoreCode == code)
            .WhereIF(allowedStores != null, t => allowedList.Contains(t.StoreCode))
            .OrderBy(t => t.CapturedAtUtc, OrderByType.Desc)
            .Skip((number - 1) * size)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var pageRows = rows.Take(size).ToList();
        var dtos = await BuildTaskDtosAsync(pageRows, actor, cancellationToken);
        return new FaceMatchPageDto<FaceUnwelcomeTaskDto>
        {
            Items = dtos,
            Page = number,
            PageSize = size,
            HasMore = rows.Count > size,
        };
    }

    public async Task<FaceUnwelcomeTaskDto> ReviewTaskAsync(
        string taskId, FaceUnwelcomeReviewRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var verdict = request.Verdict?.Trim();
        if (verdict is not ("match" or "notMatch"))
        {
            throw FaceMatchErrors.Invalid("verdict", "复核结论只能是 match（是同一个人）或 notMatch（不是）。");
        }

        var task = await RequireTaskAsync(taskId, actor, cancellationToken);
        if (task.ReviewStatus != FaceMatchReviewStatus.Pending)
        {
            throw new FaceMatchException(409, "FACE_MATCH_TASK_CLOSED", "该任务已经复核结束。");
        }

        if (string.IsNullOrEmpty(task.ImageObjectKey))
        {
            // 命中图已到期删除：没有可对照的现场图，不能再复核。
            throw FaceMatchErrors.ImageGone();
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        // 一个人就够：判断一次即结案。驳回后命中图不再有留存必要：提前到期，由后台任务删除（只保留元数据与分数用于误报统计）。
        var dismissed = verdict == "notMatch";
        var imageDeleteAt = dismissed && task.ImageDeleteAtUtc > nowUtc ? nowUtc : task.ImageDeleteAtUtc;
        // 表达式里只放局部变量：SqlSugar 会把三元表达式 / 方法调用翻译成 SQL。
        var newStatus = dismissed ? FaceMatchReviewStatus.Dismissed : FaceMatchReviewStatus.Confirmed;
        var pendingStatus = FaceMatchReviewStatus.Pending;
        var actorId = actor.Id;
        string step;
        int rows;
        if (string.IsNullOrEmpty(task.FirstReviewerId))
        {
            step = "first";
            rows = await _db.Updateable<FaceMatchTask>()
                .SetColumns(t => new FaceMatchTask
                {
                    FirstReviewerId = actorId,
                    FirstReviewedAtUtc = nowUtc,
                    ReviewStatus = newStatus,
                    ImageDeleteAtUtc = imageDeleteAt,
                })
                .Where(t => t.TaskId == task.TaskId && t.ReviewStatus == pendingStatus && t.FirstReviewerId == null)
                .ExecuteCommandAsync(cancellationToken);
            task.FirstReviewerId = actor.Id;
            task.FirstReviewedAtUtc = nowUtc;
        }
        else
        {
            // 旧版遗留：第一复核人已判断「是同一个人」、还在等第二个人（两人复核已取消）。
            // 现在由当前这位复核人直接结案（允许就是第一复核人本人），记在 Second* 列里留痕。
            step = "legacy-second";
            var firstReviewer = task.FirstReviewerId;
            rows = await _db.Updateable<FaceMatchTask>()
                .SetColumns(t => new FaceMatchTask
                {
                    SecondReviewerId = actorId,
                    SecondReviewedAtUtc = nowUtc,
                    ReviewStatus = newStatus,
                    ImageDeleteAtUtc = imageDeleteAt,
                })
                .Where(t => t.TaskId == task.TaskId
                    && t.ReviewStatus == pendingStatus
                    && t.SecondReviewerId == null
                    && t.FirstReviewerId == firstReviewer)
                .ExecuteCommandAsync(cancellationToken);
            task.SecondReviewerId = actor.Id;
            task.SecondReviewedAtUtc = nowUtc;
        }

        task.ReviewStatus = newStatus;
        task.ImageDeleteAtUtc = imageDeleteAt;

        if (rows == 0)
        {
            throw new FaceMatchException(409, "FACE_MATCH_TASK_CHANGED", "任务刚刚被其他人复核，请刷新后重试。");
        }

        await audit.WriteAsync(actor, FaceMatchAuditActions.TaskReview, task.StoreCode, task.TaskId, $"{step}:{verdict}", cancellationToken);
        return (await BuildTaskDtosAsync([task], actor, cancellationToken))[0];
    }

    public async Task<FaceMatchImageContent> GetTaskImageAsync(string taskId, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        string? storeCode = null;
        try
        {
            var task = await RequireTaskAsync(taskId, actor, cancellationToken);
            storeCode = task.StoreCode;
            if (!FaceMatchObjectKeys.IsAllowedHitImageKey(task.ImageObjectKey))
            {
                throw FaceMatchErrors.ImageGone();
            }

            var content = await ReadImageAsync(task.ImageObjectKey, cancellationToken);
            await audit.WriteAsync(actor, FaceMatchAuditActions.TaskViewCapture, task.StoreCode, task.TaskId, "ok", cancellationToken);
            return content;
        }
        catch (FaceMatchException ex) when (ex.Code != "FACE_MATCH_AUDIT_FAILED")
        {
            await TryAuditRejectedAsync(actor, FaceMatchAuditActions.TaskViewCapture, storeCode, taskId, ex.Code);
            throw;
        }
    }

    public async Task<FaceMatchImageContent> GetReferencePhotoAsync(
        string taskId, int index, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        string? storeCode = null;
        var targetRef = $"{taskId}:{index}";
        try
        {
            var task = await RequireTaskAsync(taskId, actor, cancellationToken);
            storeCode = task.StoreCode;
            var entry = await _db.Queryable<FaceUnwelcomeEntry>().FirstAsync(e => e.EntryId == task.EntryId, cancellationToken)
                ?? throw FaceMatchErrors.ImageGone();
            // 条目已撤回 / 过期时照片已清理（或即将清理），不再提供查看。
            if (entry.Status is not (FaceUnwelcomeEntryStatus.Active or FaceUnwelcomeEntryStatus.Pending))
            {
                throw FaceMatchErrors.ImageGone();
            }

            var keys = FaceMatchRules.ParseKeys(entry.PhotoObjectKeysJson);
            if (index < 0 || index >= keys.Count || !FaceMatchObjectKeys.IsSafeKey(keys[index]))
            {
                throw FaceMatchErrors.ImageGone();
            }

            var content = await ReadImageAsync(keys[index], cancellationToken);
            await audit.WriteAsync(actor, FaceMatchAuditActions.TaskViewReference, task.StoreCode, targetRef, "ok", cancellationToken);
            return content;
        }
        catch (FaceMatchException ex) when (ex.Code != "FACE_MATCH_AUDIT_FAILED")
        {
            await TryAuditRejectedAsync(actor, FaceMatchAuditActions.TaskViewReference, storeCode, targetRef, ex.Code);
            throw;
        }
    }

    public async Task<FaceUnwelcomeStatsDto> GetStatsAsync(FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var allowedStores = await GetAllowedStoresAsync(actor, cancellationToken);
        var allowedList = allowedStores?.ToList() ?? [];
        var tasks = await _db.Queryable<FaceMatchTask>()
            .WhereIF(allowedStores != null, t => allowedList.Contains(t.StoreCode))
            .Select(t => new { t.EntryId, t.Score, t.ReviewStatus })
            .ToListAsync(cancellationToken);

        var entryIds = tasks.Select(t => t.EntryId).Distinct().ToList();
        var statuses = entryIds.Count == 0
            ? new Dictionary<string, byte>()
            : (await _db.Queryable<FaceUnwelcomeEntry>()
                    .Where(e => entryIds.Contains(e.EntryId))
                    .Select(e => new { e.EntryId, e.Status })
                    .ToListAsync(cancellationToken))
                .ToDictionary(e => e.EntryId, e => e.Status);

        var entries = tasks
            .GroupBy(t => t.EntryId)
            .Select(g =>
            {
                var dismissed = g.Count(t => t.ReviewStatus == FaceMatchReviewStatus.Dismissed);
                var confirmed = g.Count(t => t.ReviewStatus == FaceMatchReviewStatus.Confirmed);
                var closed = dismissed + confirmed;
                decimal? rate = closed == 0 ? null : Math.Round((decimal)dismissed / closed, 4);
                return new FaceUnwelcomeStatsEntryDto
                {
                    EntryId = g.Key,
                    StatusName = statuses.TryGetValue(g.Key, out var s) ? FaceMatchRules.EntryStatusName(s) : "unknown",
                    TaskCount = g.Count(),
                    PendingCount = g.Count(t => t.ReviewStatus == FaceMatchReviewStatus.Pending),
                    DismissedCount = dismissed,
                    ConfirmedCount = confirmed,
                    DismissRate = rate,
                    SuggestReview = closed >= StatsMinReviewedForFlag && rate >= StatsDismissRateForFlag,
                };
            })
            .OrderByDescending(e => e.SuggestReview)
            .ThenByDescending(e => e.TaskCount)
            .ToList();

        // 每 5 分一档（0–4 … 95–100，最后一档含 100），用于看驳回率随相似度的变化来调阈值。
        var buckets = new List<FaceUnwelcomeStatsBucketDto>();
        for (var from = 0; from < 100; from += 5)
        {
            var low = from;
            var high = from + 5;
            var inBucket = tasks.Where(t => t.Score >= low && (t.Score < high || (high == 100 && t.Score <= 100))).ToList();
            buckets.Add(new FaceUnwelcomeStatsBucketDto
            {
                From = low,
                To = high == 100 ? 100 : high - 1,
                Total = inBucket.Count,
                DismissedCount = inBucket.Count(t => t.ReviewStatus == FaceMatchReviewStatus.Dismissed),
                ConfirmedCount = inBucket.Count(t => t.ReviewStatus == FaceMatchReviewStatus.Confirmed),
            });
        }

        var totalDismissed = tasks.Count(t => t.ReviewStatus == FaceMatchReviewStatus.Dismissed);
        var totalConfirmed = tasks.Count(t => t.ReviewStatus == FaceMatchReviewStatus.Confirmed);
        return new FaceUnwelcomeStatsDto
        {
            TaskCount = tasks.Count,
            PendingCount = tasks.Count(t => t.ReviewStatus == FaceMatchReviewStatus.Pending),
            DismissedCount = totalDismissed,
            ConfirmedCount = totalConfirmed,
            DismissRate = totalDismissed + totalConfirmed == 0
                ? null
                : Math.Round((decimal)totalDismissed / (totalDismissed + totalConfirmed), 4),
            Entries = entries,
            ScoreBuckets = buckets,
        };
    }

    // ---------- 内部辅助 ----------

    private async Task<FaceMatchImageContent> ReadImageAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await objectStore.ReadAsync(objectKey, FaceMatchRules.MaxImageBytes, cancellationToken);
            var image = FaceMatchRules.DetectImage(bytes) ?? throw new FaceMatchException(
                415, "FACE_MATCH_IMAGE_UNSUPPORTED", "图片类型不受支持，已拒绝。");
            return new FaceMatchImageContent(bytes, image.ContentType);
        }
        catch (FaceMatchStoreException ex)
        {
            throw FaceMatchErrors.FromStore(ex);
        }
    }

    /// <summary>被拒绝的查看尝试也记审计（Result 为错误码）；审计本身写失败不能盖掉原始错误。</summary>
    private async Task TryAuditRejectedAsync(FaceMatchActor actor, string action, string? storeCode, string targetRef, string code)
    {
        try
        {
            await audit.WriteAsync(actor, action, storeCode, targetRef, code, CancellationToken.None);
        }
        catch (FaceMatchException)
        {
            // 审计写入失败已在 FaceMatchAudit 里记 Error 日志。
        }
    }

    private async Task<List<FaceUnwelcomeTaskDto>> BuildTaskDtosAsync(
        IReadOnlyList<FaceMatchTask> tasks, FaceMatchActor actor, CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        var storeCodes = tasks.Select(t => t.StoreCode).Distinct().ToList();
        var storeNames = (await _db.Queryable<Store>()
                .Where(s => storeCodes.Contains(s.StoreCode) && !s.IsDeleted)
                .Select(s => new { s.StoreCode, s.StoreName })
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.StoreCode)
            .ToDictionary(g => g.Key, g => g.First().StoreName);
        var entryIds = tasks.Select(t => t.EntryId).Distinct().ToList();
        var entries = (await _db.Queryable<FaceUnwelcomeEntry>().Where(e => entryIds.Contains(e.EntryId)).ToListAsync(cancellationToken))
            .ToDictionary(e => e.EntryId);
        var names = await FaceMatchErrors.LoadUserNamesAsync(
            _db, tasks.SelectMany(t => new[] { t.FirstReviewerId, t.SecondReviewerId }), cancellationToken);
        var highBand = settingsProvider.Options.Unwelcome.HighBandScore;
        var allowedStores = await GetAllowedStoresAsync(actor, cancellationToken);

        return tasks.Select(t =>
        {
            entries.TryGetValue(t.EntryId, out var entry);
            var referenceCount = entry is { Status: FaceUnwelcomeEntryStatus.Active or FaceUnwelcomeEntryStatus.Pending }
                ? FaceMatchRules.ParseKeys(entry.PhotoObjectKeysJson).Count
                : 0;
            var imageAvailable = !string.IsNullOrEmpty(t.ImageObjectKey);
            var pending = t.ReviewStatus == FaceMatchReviewStatus.Pending;
            // 一个人就够：待复核的任务一律是 first（含旧版遗留的「已一审待二审」任务），不再返回 second，也不再排除第一复核人。
            var nextStep = pending ? "first" : "none";
            var inScope = allowedStores == null || allowedStores.Contains(t.StoreCode);
            var canReview = pending && imageAvailable && inScope;
            return new FaceUnwelcomeTaskDto
            {
                TaskId = t.TaskId,
                StoreCode = t.StoreCode,
                StoreName = storeNames.GetValueOrDefault(t.StoreCode),
                CameraId = t.CameraId,
                CapturedAtUtc = FaceMatchRules.AsUtc(t.CapturedAtUtc),
                EntryId = t.EntryId,
                Score = t.Score,
                ScorePercent = (int)Math.Clamp(Math.Round(t.Score, MidpointRounding.AwayFromZero), 0, 100),
                Band = t.Score >= highBand ? "high" : "normal",
                ReviewStatus = t.ReviewStatus,
                ReviewStatusName = FaceMatchRules.ReviewStatusName(t.ReviewStatus),
                FirstReviewerName = t.FirstReviewerId != null ? names.GetValueOrDefault(t.FirstReviewerId) : null,
                FirstReviewedAtUtc = FaceMatchRules.AsUtc(t.FirstReviewedAtUtc),
                SecondReviewerName = t.SecondReviewerId != null ? names.GetValueOrDefault(t.SecondReviewerId) : null,
                SecondReviewedAtUtc = FaceMatchRules.AsUtc(t.SecondReviewedAtUtc),
                ImageAvailable = imageAvailable,
                ImageDeleteAtUtc = FaceMatchRules.AsUtc(t.ImageDeleteAtUtc),
                ReferencePhotoCount = referenceCount,
                NextStep = nextStep,
                CanReview = canReview,
            };
        }).ToList();
    }

    /// <summary>取任务并校验门店范围（非管理员只能看自己门店的任务）；不存在与越权共用同一个 404，不泄露「某个编号是否存在」。</summary>
    private async Task<FaceMatchTask> RequireTaskAsync(string taskId, FaceMatchActor actor, CancellationToken cancellationToken)
    {
        if (!FaceMatchObjectKeys.IsValidId(taskId))
        {
            throw FaceMatchErrors.NotFound("任务");
        }

        var task = await _db.Queryable<FaceMatchTask>().FirstAsync(t => t.TaskId == taskId, cancellationToken)
            ?? throw FaceMatchErrors.NotFound("任务");
        var allowed = await GetAllowedStoresAsync(actor, cancellationToken);
        if (allowed != null && !allowed.Contains(task.StoreCode))
        {
            throw FaceMatchErrors.NotFound("任务");
        }

        return task;
    }

    private async Task<FaceUnwelcomeEntry> RequireEntryAsync(string entryId, FaceMatchActor actor, CancellationToken cancellationToken)
    {
        if (!FaceMatchObjectKeys.IsValidId(entryId))
        {
            throw FaceMatchErrors.NotFound("条目");
        }

        var entry = await _db.Queryable<FaceUnwelcomeEntry>().FirstAsync(e => e.EntryId == entryId, cancellationToken)
            ?? throw FaceMatchErrors.NotFound("条目");
        var allowed = await GetAllowedStoresAsync(actor, cancellationToken);
        if (allowed != null && !FaceMatchRules.SplitStoreScope(entry.StoreScope).Any(allowed.Contains))
        {
            throw FaceMatchErrors.NotFound("条目");
        }

        return entry;
    }

    /// <summary>非管理员可见的门店编码集合；管理员返回 null（不限）。</summary>
    private async Task<HashSet<string>?> GetAllowedStoresAsync(FaceMatchActor actor, CancellationToken cancellationToken)
    {
        if (actor.IsAdmin)
        {
            return null;
        }

        var codes = await _db.Queryable<UserStore>()
            .InnerJoin<Store>((link, store) => link.StoreGUID == store.StoreGUID)
            .Where((link, store) => link.UserGUID == actor.Id && !link.IsDeleted && !store.IsDeleted)
            .Select((link, store) => store.StoreCode)
            .ToListAsync(cancellationToken);
        return codes.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<List<string>> NormalizeStoreScopeAsync(
        IReadOnlyList<string>? requested, FaceMatchActor actor, CancellationToken cancellationToken)
    {
        var codes = (requested ?? [])
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0 || codes.Count > FaceMatchRules.MaxStoreScopeStores)
        {
            throw FaceMatchErrors.Invalid("storeScope", $"请选择 1–{FaceMatchRules.MaxStoreScopeStores} 个生效门店。");
        }

        if (codes.Any(c => !BlazorApp.Api.Features.FaceCameras.FaceCameraRules.IsValidStoreCode(c)))
        {
            throw FaceMatchErrors.Invalid("storeScope", "门店编码格式不正确。");
        }

        var existing = (await _db.Queryable<Store>()
                .Where(s => codes.Contains(s.StoreCode) && !s.IsDeleted)
                .Select(s => s.StoreCode)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (codes.Any(c => !existing.Contains(c)))
        {
            throw FaceMatchErrors.Invalid("storeScope", "存在不存在或已停用的门店。");
        }

        var allowed = await GetAllowedStoresAsync(actor, cancellationToken);
        if (allowed != null && codes.Any(c => !allowed.Contains(c)))
        {
            throw new FaceMatchException(403, "FACE_MATCH_STORE_FORBIDDEN", "只能为自己所属门店提交条目。");
        }

        return codes.OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    private async Task FillNamesAsync(Dictionary<string, string> names, IEnumerable<FaceUnwelcomeEntry> entries, CancellationToken cancellationToken)
    {
        var loaded = await FaceMatchErrors.LoadUserNamesAsync(
            _db, entries.SelectMany(e => new[] { e.SubmittedBy, e.ApprovedBy, e.SecondApprovedBy }), cancellationToken);
        foreach (var pair in loaded)
        {
            names[pair.Key] = pair.Value;
        }
    }

    private async Task<bool> TryDeleteObjectsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var allDeleted = true;
        foreach (var key in keys)
        {
            try
            {
                await objectStore.DeleteAsync(key, cancellationToken);
            }
            catch (FaceMatchStoreException ex)
            {
                allDeleted = false;
                logger.LogWarning("名单参考照片删除失败，稍后由后台任务重试: Kind={Kind}", ex.Kind);
            }
        }

        return allDeleted;
    }

    private async Task TryDeletePersonAsync(string personId)
    {
        try
        {
            await library.DeletePersonAsync(personId, CancellationToken.None);
        }
        catch (FaceLibraryException ex)
        {
            logger.LogError("名单入库失败后回滚腾讯云人员失败，需人工核对: PersonId={PersonId}, Code={Code}", personId, ex.Code);
        }
    }

    private static DateTime ReviewDue(DateTime expiresAtUtc, DateTime nowUtc)
    {
        var due = expiresAtUtc.AddDays(-FaceMatchRules.ExpiryReminderDays);
        return due < nowUtc ? nowUtc : due;
    }

    /// <summary>
    /// 条目需要的批准数：Other 为 2。已生效但只有一位批准人（管理员单人批准）的 Other 条目按实际的 1 显示，
    /// 避免页面出现「已生效 1 / 2」的矛盾。
    /// </summary>
    private static int ApprovalsNeeded(FaceUnwelcomeEntry entry) =>
        entry.IncidentCategory == FaceIncidentCategory.Other
            && !(entry.Status == FaceUnwelcomeEntryStatus.Active && entry.SecondApprovedBy == null)
            ? 2
            : 1;

    private static int ApprovalsDone(FaceUnwelcomeEntry entry) =>
        (entry.ApprovedBy != null ? 1 : 0) + (entry.SecondApprovedBy != null ? 1 : 0);

    // 状态筛选同时接受名称与数值（条目 0–3、任务 0–2），与 Web 页面传数字的做法兼容。
    private static byte? ParseEntryStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => null,
        "pending" or "0" => FaceUnwelcomeEntryStatus.Pending,
        "active" or "1" => FaceUnwelcomeEntryStatus.Active,
        "expired" or "2" => FaceUnwelcomeEntryStatus.Expired,
        "withdrawn" or "3" => FaceUnwelcomeEntryStatus.Withdrawn,
        _ => throw FaceMatchErrors.Invalid("status", "状态筛选只能是 pending / active / expired / withdrawn（或 0–3）。"),
    };

    private static byte? ParseReviewStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => null,
        "pending" or "0" => FaceMatchReviewStatus.Pending,
        "dismissed" or "1" => FaceMatchReviewStatus.Dismissed,
        "confirmed" or "2" => FaceMatchReviewStatus.Confirmed,
        _ => throw FaceMatchErrors.Invalid("status", "状态筛选只能是 pending / dismissed / confirmed（或 0–2）。"),
    };

    private FaceUnwelcomeEntryDto ToDto(
        FaceUnwelcomeEntry e, IReadOnlyDictionary<string, string> names, FaceMatchActor actor, bool canApprove)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var needed = ApprovalsNeeded(e);
        var done = ApprovalsDone(e);
        var daysToExpiry = (int)Math.Floor((e.ExpiresAtUtc - nowUtc).TotalDays);
        var photoKeys = FaceMatchRules.ParseKeys(e.PhotoObjectKeysJson);
        var active = e.Status == FaceUnwelcomeEntryStatus.Active;
        return new FaceUnwelcomeEntryDto
        {
            EntryId = e.EntryId,
            IncidentCategory = e.IncidentCategory,
            IncidentCategoryName = FaceMatchRules.CategoryName(e.IncidentCategory),
            CategoryNote = e.CategoryNote,
            EvidenceRef = e.EvidenceRef,
            StoreScope = FaceMatchRules.SplitStoreScope(e.StoreScope),
            SubmittedById = e.SubmittedBy,
            SubmittedByName = names.GetValueOrDefault(e.SubmittedBy) ?? e.SubmittedBy,
            SubmittedAtUtc = FaceMatchRules.AsUtc(e.SubmittedAtUtc),
            ApprovedByName = e.ApprovedBy != null ? names.GetValueOrDefault(e.ApprovedBy) : null,
            ApprovedAtUtc = FaceMatchRules.AsUtc(e.ApprovedAtUtc),
            SecondApprovedByName = e.SecondApprovedBy != null ? names.GetValueOrDefault(e.SecondApprovedBy) : null,
            SecondApprovedAtUtc = FaceMatchRules.AsUtc(e.SecondApprovedAtUtc),
            ApprovalsNeeded = needed,
            ApprovalsDone = done,
            ReviewDueAtUtc = FaceMatchRules.AsUtc(e.ReviewDueAtUtc),
            ExpiresAtUtc = FaceMatchRules.AsUtc(e.ExpiresAtUtc),
            Status = e.Status,
            StatusName = FaceMatchRules.EntryStatusName(e.Status),
            PhotoCount = photoKeys.Count,
            DisputeNote = e.DisputeNote,
            ExpiringSoon = active && e.ExpiresAtUtc - nowUtc <= TimeSpan.FromDays(FaceMatchRules.ExpiryReminderDays),
            DaysToExpiry = daysToExpiry,
            // 管理员一个人批准即可生效：不排除自己提交的，也不排除自己已批准过一次的；非管理员两条都排除。
            CanApprove = canApprove
                && e.Status == FaceUnwelcomeEntryStatus.Pending
                && (actor.IsAdmin
                    || (!string.Equals(e.SubmittedBy, actor.Id, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(e.ApprovedBy, actor.Id, StringComparison.OrdinalIgnoreCase))),
            CleanupPending = e.Status is FaceUnwelcomeEntryStatus.Expired or FaceUnwelcomeEntryStatus.Withdrawn && photoKeys.Count > 0,
        };
    }
}
