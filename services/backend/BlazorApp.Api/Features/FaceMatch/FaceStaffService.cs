using BlazorApp.Api.Data;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 员工同意与登记（设计第 7 节、13.4）：
/// 1. 登记必须先有有效（未撤回）的同意记录，且同意属于该员工；每人同时只有一条有效登记（库里过滤唯一索引兜底）；
/// 2. NSW 员工登记前须有满 14 天的书面通知日期，不满拒绝；
/// 3. 登记照（3–5 张 jpeg/png，单张 ≤5MB）按字节头判定类型，存私有桶 face-match/enrollment/{enrollmentId}/，
///    腾讯云只收到照片与不透明编号（PersonName 也只是编号，不含姓名 / 员工编号）；
/// 4. 任一步失败都会回滚已做的部分（DeletePerson + 删已上传照片）；
/// 5. 撤回：先 DeletePerson，成功后标记 Withdrawn 并删登记照；腾讯云删不掉时登记保持 Active 并标记「待删除」，
///    后台任务继续重试（凭据缺失时同样），不会因为删不掉而长期留存或谎报已撤回。
/// 每个写动作写持久审计，审计内容只有编号。
/// </summary>
public sealed class FaceStaffService(
    SqlSugarContext context,
    IFaceLibrary library,
    IFaceMatchObjectStore objectStore,
    IFaceMatchSettingsProvider settingsProvider,
    IFaceMatchAudit audit,
    TimeProvider timeProvider,
    ILogger<FaceStaffService> logger
) : IFaceStaffService
{
    private const int MaxStaffList = 200;
    private const int MaxConsentVersionLength = 32;
    private const int MaxReasonLength = 200;

    private readonly ISqlSugarClient _db = context.Db;

    // ---------- 员工候选 ----------

    public async Task<IReadOnlyList<FaceStaffCandidateDto>> ListStaffAsync(
        string storeCode, string? keyword, CancellationToken cancellationToken = default)
    {
        var code = storeCode?.Trim();
        if (!BlazorApp.Api.Features.FaceCameras.FaceCameraRules.IsValidStoreCode(code))
        {
            throw FaceMatchErrors.Invalid("storeCode", "门店编码格式不正确。");
        }

        var key = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        if (key is { Length: > 50 })
        {
            throw FaceMatchErrors.Invalid("keyword", "关键字过长。");
        }

        var query = _db.Queryable<UserStore>()
            .InnerJoin<Store>((link, store) => link.StoreGUID == store.StoreGUID)
            .InnerJoin<User>((link, store, user) => link.UserGUID == user.UserGUID)
            .Where((link, store, user) =>
                store.StoreCode == code && !store.IsDeleted && !link.IsDeleted && user.IsActive && !user.IsDeleted);
        if (key != null)
        {
            query = query.Where((link, store, user) =>
                (user.FullName != null && user.FullName.Contains(key))
                || (user.GivenName != null && user.GivenName.Contains(key))
                || (user.MiddleName != null && user.MiddleName.Contains(key))
                || (user.FamilyName != null && user.FamilyName.Contains(key))
                || (user.Nickname != null && user.Nickname.Contains(key))
                || user.Username.Contains(key)
                || user.Email.Contains(key));
        }

        var users = await query
            .Select((link, store, user) => user)
            .Distinct()
            .Take(MaxStaffList)
            .ToListAsync(cancellationToken);
        if (users.Count == 0)
        {
            return [];
        }

        var guids = users.Select(u => u.UserGUID).ToList();
        var activeUsers = (await _db.Queryable<FaceEnrollment>()
                .Where(e => guids.Contains(e.UserGuid) && e.Status == FaceEnrollmentStatus.Active)
                .Select(e => e.UserGuid)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nswUsers = await FindNswUsersAsync(guids, cancellationToken);

        return users
            .OrderBy(u => FaceMatchErrors.DisplayName(u), StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new FaceStaffCandidateDto
            {
                UserGuid = u.UserGUID,
                DisplayName = FaceMatchErrors.DisplayName(u),
                Email = string.IsNullOrWhiteSpace(u.Email) ? null : u.Email,
                EnrollmentState = activeUsers.Contains(u.UserGUID) ? "active" : "none",
                RequiresNswNotice = nswUsers.Contains(u.UserGUID),
            })
            .ToList();
    }

    // ---------- 同意 ----------

    public async Task<IReadOnlyList<FaceConsentDto>> ListConsentsAsync(
        string? userGuid, bool includeWithdrawn, CancellationToken cancellationToken = default)
    {
        var uid = userGuid?.Trim();
        var rows = await _db.Queryable<FaceConsentRecord>()
            .WhereIF(!string.IsNullOrEmpty(uid), c => c.UserGuid == uid)
            .WhereIF(!includeWithdrawn, c => c.WithdrawnAtUtc == null)
            .OrderBy(c => c.CreatedAtUtc, OrderByType.Desc)
            .Take(500)
            .ToListAsync(cancellationToken);
        var names = await FaceMatchErrors.LoadUserNamesAsync(_db, rows.Select(r => r.UserGuid), cancellationToken);
        return rows.Select(r => ToDto(r, names.GetValueOrDefault(r.UserGuid))).ToList();
    }

    public async Task<FaceConsentDto> CreateConsentAsync(
        FaceConsentCreateRequest request, FaceMatchUpload? scan, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var userGuid = request.UserGuid?.Trim();
        if (string.IsNullOrEmpty(userGuid) || userGuid.Length > 50)
        {
            throw FaceMatchErrors.Invalid("userGuid", "请选择员工。");
        }

        if (!FaceMatchRules.TryNormalizeText(request.ConsentVersion, MaxConsentVersionLength, out var version) || version == null)
        {
            throw FaceMatchErrors.Invalid("consentVersion", "请填写同意书版本（最长 32 位）。");
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (request.SignedAtUtc is not DateTime signedAt)
        {
            throw FaceMatchErrors.Invalid("signedAtUtc", "请填写签署时间。");
        }

        var signedAtUtc = DateTime.SpecifyKind(signedAt.ToUniversalTime(), DateTimeKind.Utc);
        if (signedAtUtc > nowUtc.AddDays(1))
        {
            throw FaceMatchErrors.Invalid("signedAtUtc", "签署时间不能晚于当前时间。");
        }

        var method = request.Method?.Trim().ToLowerInvariant();
        if (method is not ("paper" or "electronic"))
        {
            throw FaceMatchErrors.Invalid("method", "签署方式只能是 paper（纸质）或 electronic（电子签）。");
        }

        if (!FaceMatchRules.TryParseDate(request.NswNoticeDate, out var nswNotice))
        {
            throw FaceMatchErrors.Invalid("nswNoticeDate", "NSW 通知日期必须是 yyyy-MM-dd 格式。");
        }

        var user = await _db.Queryable<User>().FirstAsync(u => u.UserGUID == userGuid && u.IsActive && !u.IsDeleted, cancellationToken)
            ?? throw FaceMatchErrors.NotFound("员工");

        // NSW 员工监控通知：登记前至少提前 14 天书面通知；不满拒绝（条文原文未核实，【待律师确认】）。
        if ((await FindNswUsersAsync([userGuid], cancellationToken)).Contains(userGuid))
        {
            EnsureNswNoticeSufficient(nswNotice);
        }

        DetectedImage? detected = null;
        if (scan != null)
        {
            if (scan.Bytes.Length == 0 || scan.Bytes.Length > FaceMatchRules.MaxConsentScanBytes)
            {
                throw FaceMatchErrors.Invalid("scan", "同意书扫描件为空或超过 10MB。");
            }

            detected = FaceMatchRules.DetectDocument(scan.Bytes)
                ?? throw FaceMatchErrors.Invalid("scan", "同意书扫描件只支持 PDF、JPG 或 PNG。");
        }
        else if (method == "paper")
        {
            throw FaceMatchErrors.Invalid("scan", "纸质同意必须上传扫描件。");
        }

        var consentId = FaceMatchRules.NewId();
        string? evidenceKey = null;
        if (scan != null && detected is { } doc)
        {
            if (!objectStore.IsConfigured)
            {
                throw FaceMatchErrors.StoreNotConfigured();
            }

            evidenceKey = FaceMatchObjectKeys.Consent(consentId, doc.Extension);
            await PutAsync(evidenceKey, scan.Bytes, doc.ContentType, cancellationToken);
        }

        var entity = new FaceConsentRecord
        {
            ConsentId = consentId,
            UserGuid = user.UserGUID,
            ConsentVersion = version,
            SignedAtUtc = signedAtUtc,
            Method = method,
            EvidenceObjectKey = evidenceKey,
            NswNoticeDate = nswNotice,
            CreatedAtUtc = nowUtc,
            CreatedBy = FaceMatchAudit.Sanitize(actor.Name, 100) ?? "unknown",
        };
        try
        {
            await _db.Insertable(entity).ExecuteCommandAsync(cancellationToken);
        }
        catch
        {
            // 记录没写成：把刚上传的扫描件删掉，避免留下没有记录指向的孤儿文件。
            if (evidenceKey != null)
            {
                await TryDeleteObjectsAsync([evidenceKey], CancellationToken.None);
            }

            throw;
        }

        await audit.WriteAsync(actor, FaceMatchAuditActions.ConsentCreate, null, consentId, "ok", cancellationToken);
        return ToDto(entity, FaceMatchErrors.DisplayName(user));
    }

    public async Task<FaceConsentWithdrawResultDto> WithdrawConsentAsync(
        string consentId, FaceConsentWithdrawRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        if (!FaceMatchRules.TryNormalizeText(request.Reason, MaxReasonLength, out var reason))
        {
            throw FaceMatchErrors.Invalid("reason", "撤回原因过长或含非法字符（最长 200）。");
        }

        var consent = await RequireConsentAsync(consentId, cancellationToken);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (consent.WithdrawnAtUtc == null)
        {
            consent.WithdrawnAtUtc = nowUtc;
            consent.WithdrawnReason = reason;
            await _db.Updateable(consent)
                .UpdateColumns(c => new { c.WithdrawnAtUtc, c.WithdrawnReason })
                .ExecuteCommandAsync(cancellationToken);
        }

        // 同意撤回后立即对应删除腾讯云人员与登记照；删不掉的留给后台任务重试（enrollment 仍是 Active，页面显示待删除）。
        var enrollments = await _db.Queryable<FaceEnrollment>()
            .Where(e => e.ConsentId == consent.ConsentId && e.Status == FaceEnrollmentStatus.Active)
            .ToListAsync(cancellationToken);
        var withdrawn = 0;
        var pending = 0;
        foreach (var enrollment in enrollments)
        {
            if (await TryWithdrawEnrollmentAsync(enrollment, cancellationToken)) withdrawn++; else pending++;
        }

        await audit.WriteAsync(
            actor, FaceMatchAuditActions.ConsentWithdraw, null, consent.ConsentId,
            pending == 0 ? "ok" : "ok-pending-remote-deletion", cancellationToken);

        var names = await FaceMatchErrors.LoadUserNamesAsync(_db, [consent.UserGuid], cancellationToken);
        return new FaceConsentWithdrawResultDto
        {
            Consent = ToDto(consent, names.GetValueOrDefault(consent.UserGuid)),
            WithdrawnEnrollmentCount = withdrawn,
            PendingRemoteDeletionCount = pending,
        };
    }

    // ---------- 登记 ----------

    public async Task<IReadOnlyList<FaceEnrollmentDto>> ListEnrollmentsAsync(
        string? userGuid, bool activeOnly, CancellationToken cancellationToken = default)
    {
        var uid = userGuid?.Trim();
        var rows = await _db.Queryable<FaceEnrollment>()
            .WhereIF(!string.IsNullOrEmpty(uid), e => e.UserGuid == uid)
            .WhereIF(activeOnly, e => e.Status == FaceEnrollmentStatus.Active)
            .OrderBy(e => e.EnrolledAtUtc, OrderByType.Desc)
            .Take(500)
            .ToListAsync(cancellationToken);
        var names = await FaceMatchErrors.LoadUserNamesAsync(_db, rows.Select(r => r.UserGuid), cancellationToken);
        var consentIds = rows.Select(r => r.ConsentId).Distinct().ToList();
        var withdrawnConsents = consentIds.Count == 0
            ? new HashSet<string>()
            : (await _db.Queryable<FaceConsentRecord>()
                    .Where(c => consentIds.Contains(c.ConsentId) && c.WithdrawnAtUtc != null)
                    .Select(c => c.ConsentId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
        return rows.Select(r => ToDto(r, names.GetValueOrDefault(r.UserGuid), withdrawnConsents.Contains(r.ConsentId))).ToList();
    }

    public async Task<FaceEnrollmentDto> CreateEnrollmentAsync(
        FaceEnrollmentCreateRequest request, IReadOnlyList<FaceMatchUpload> photos, FaceMatchActor actor, CancellationToken cancellationToken = default)
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

        var userGuid = request.UserGuid?.Trim();
        var consentId = request.ConsentId?.Trim();
        if (string.IsNullOrEmpty(userGuid) || userGuid.Length > 50)
        {
            throw FaceMatchErrors.Invalid("userGuid", "请选择员工。");
        }

        if (!FaceMatchObjectKeys.IsValidId(consentId))
        {
            throw FaceMatchErrors.Invalid("consentId", "请选择有效的同意记录。");
        }

        if (photos.Count is < FaceMatchRules.MinEnrollmentPhotos or > FaceMatchRules.MaxEnrollmentPhotos)
        {
            throw FaceMatchErrors.Invalid("photos", $"登记照需要 {FaceMatchRules.MinEnrollmentPhotos}–{FaceMatchRules.MaxEnrollmentPhotos} 张。");
        }

        var detected = new List<DetectedImage>();
        foreach (var photo in photos)
        {
            if (photo.Bytes.Length == 0 || photo.Bytes.Length > FaceMatchRules.MaxImageBytes)
            {
                throw FaceMatchErrors.Invalid("photos", "登记照为空或单张超过 5MB。");
            }

            detected.Add(FaceMatchRules.DetectImage(photo.Bytes)
                ?? throw FaceMatchErrors.Invalid("photos", "登记照只支持 JPG 或 PNG。"));
        }

        var user = await _db.Queryable<User>().FirstAsync(u => u.UserGUID == userGuid && u.IsActive && !u.IsDeleted, cancellationToken)
            ?? throw FaceMatchErrors.NotFound("员工");
        var consent = await _db.Queryable<FaceConsentRecord>().FirstAsync(c => c.ConsentId == consentId, cancellationToken);
        if (consent == null || !string.Equals(consent.UserGuid, user.UserGUID, StringComparison.OrdinalIgnoreCase))
        {
            throw FaceMatchErrors.NotFound("同意记录");
        }

        if (consent.WithdrawnAtUtc != null)
        {
            throw new FaceMatchException(409, "FACE_MATCH_CONSENT_WITHDRAWN", "该同意记录已撤回，需要重新取得员工同意后才能登记。");
        }

        if ((await FindNswUsersAsync([user.UserGUID], cancellationToken)).Contains(user.UserGUID))
        {
            EnsureNswNoticeSufficient(consent.NswNoticeDate);
        }

        if (await _db.Queryable<FaceEnrollment>().AnyAsync(
                e => e.UserGuid == user.UserGUID && e.Status == FaceEnrollmentStatus.Active, cancellationToken))
        {
            throw new FaceMatchException(409, "FACE_MATCH_ENROLLMENT_EXISTS", "该员工已有有效登记，请先撤回后再重新登记。");
        }

        var enrollmentId = FaceMatchRules.NewId();
        var personId = FaceMatchRules.EnrollmentPersonId(enrollmentId);
        var keys = detected.Select((d, i) => FaceMatchObjectKeys.Enrollment(enrollmentId, i, d.Extension)).ToList();
        var uploaded = new List<string>();
        var personCreated = false;
        try
        {
            await library.EnsureGroupAsync(FaceMatchRules.StaffGroupId, FaceMatchRules.StaffGroupId, cancellationToken);

            // 登记照另存私有桶：腾讯云不能导出库中人脸，将来换引擎或撤回都靠这份留存。
            for (var i = 0; i < photos.Count; i++)
            {
                await PutAsync(keys[i], photos[i].Bytes, detected[i].ContentType, cancellationToken);
                uploaded.Add(keys[i]);
            }

            await library.CreatePersonAsync(FaceMatchRules.StaffGroupId, personId, enrollmentId, photos[0].Bytes, cancellationToken);
            personCreated = true;
            var rest = photos.Skip(1).Select(p => p.Bytes).ToList();
            var created = await library.CreateFacesAsync(personId, rest, cancellationToken);
            if (created.Count < rest.Count)
            {
                throw new FaceLibraryException(
                    FaceLibraryFailureKind.InvalidImage, "PartialFaces", "部分登记照没有通过腾讯云的人脸检测。");
            }

            var entity = new FaceEnrollment
            {
                EnrollmentId = enrollmentId,
                UserGuid = user.UserGUID,
                ConsentId = consent.ConsentId,
                TencentPersonId = personId,
                PhotoObjectKeysJson = FaceMatchRules.SerializeKeys(keys),
                Status = FaceEnrollmentStatus.Active,
                EnrolledAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                EnrolledBy = FaceMatchAudit.Sanitize(actor.Name, 100) ?? "unknown",
            };
            try
            {
                await _db.Insertable(entity).ExecuteCommandAsync(cancellationToken);
            }
            catch (Exception ex) when (FaceMatchErrors.IsUniqueViolation(ex))
            {
                // 并发的另一次登记抢先写入了同一员工的有效登记：本次回滚。
                throw new FaceMatchException(409, "FACE_MATCH_ENROLLMENT_EXISTS", "该员工已有有效登记，请先撤回后再重新登记。");
            }

            await audit.WriteAsync(actor, FaceMatchAuditActions.EnrollmentCreate, null, enrollmentId, "ok", cancellationToken);
            return ToDto(entity, FaceMatchErrors.DisplayName(user), consentWithdrawn: false);
        }
        catch (FaceLibraryException ex)
        {
            await RollbackEnrollmentAsync(personId, personCreated, uploaded);
            throw FaceMatchErrors.FromLibrary(ex);
        }
        catch (FaceMatchStoreException ex)
        {
            await RollbackEnrollmentAsync(personId, personCreated, uploaded);
            throw FaceMatchErrors.FromStore(ex);
        }
        catch
        {
            await RollbackEnrollmentAsync(personId, personCreated, uploaded);
            throw;
        }
    }

    public async Task<FaceEnrollmentDto> WithdrawEnrollmentAsync(
        string enrollmentId, FaceEnrollmentWithdrawRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        if (!FaceMatchRules.TryNormalizeText(request.Reason, MaxReasonLength, out _))
        {
            throw FaceMatchErrors.Invalid("reason", "撤回原因过长或含非法字符（最长 200）。");
        }

        if (!FaceMatchObjectKeys.IsValidId(enrollmentId))
        {
            throw FaceMatchErrors.NotFound("登记");
        }

        var enrollment = await _db.Queryable<FaceEnrollment>().FirstAsync(e => e.EnrollmentId == enrollmentId, cancellationToken)
            ?? throw FaceMatchErrors.NotFound("登记");
        if (enrollment.Status == FaceEnrollmentStatus.Active)
        {
            if (!await TryWithdrawEnrollmentAsync(enrollment, cancellationToken))
            {
                // 腾讯云删除没有完成：登记保持 Active（页面显示待删除），后台任务继续重试，这里如实告诉调用方。
                await audit.WriteAsync(actor, FaceMatchAuditActions.EnrollmentWithdraw, null, enrollmentId, "pending-remote-deletion", cancellationToken);
                throw new FaceMatchException(502, "FACE_MATCH_WITHDRAW_PENDING", "腾讯云人脸库删除未完成，登记仍然有效，请稍后重试撤回。");
            }

            await audit.WriteAsync(actor, FaceMatchAuditActions.EnrollmentWithdraw, null, enrollmentId, "ok", cancellationToken);
        }

        var names = await FaceMatchErrors.LoadUserNamesAsync(_db, [enrollment.UserGuid], cancellationToken);
        return ToDto(enrollment, names.GetValueOrDefault(enrollment.UserGuid), consentWithdrawn: false);
    }

    /// <summary>
    /// 撤回一条有效登记：DeletePerson 成功（或人员本来就不在）→ 标记 Withdrawn → 尽力删登记照。
    /// 返回 false 表示腾讯云删除没完成（登记保持 Active 待重试）。登记照删不掉不影响返回 true，
    /// 但库里会保留照片键，后台任务会继续清理。
    /// </summary>
    internal async Task<bool> TryWithdrawEnrollmentAsync(FaceEnrollment enrollment, CancellationToken cancellationToken)
    {
        if (!library.IsAvailable)
        {
            return false;
        }

        try
        {
            await library.DeletePersonAsync(enrollment.TencentPersonId, cancellationToken);
        }
        catch (FaceLibraryException ex)
        {
            logger.LogWarning("撤回登记时腾讯云删除未完成: Enrollment={EnrollmentId}, Kind={Kind}, Code={Code}", enrollment.EnrollmentId, ex.Kind, ex.Code);
            return false;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        enrollment.Status = FaceEnrollmentStatus.Withdrawn;
        enrollment.WithdrawnAtUtc = nowUtc;
        await _db.Updateable(enrollment)
            .UpdateColumns(e => new { e.Status, e.WithdrawnAtUtc })
            .Where(e => e.EnrollmentId == enrollment.EnrollmentId && e.Status == FaceEnrollmentStatus.Active)
            .ExecuteCommandAsync(cancellationToken);

        var keys = FaceMatchRules.ParseKeys(enrollment.PhotoObjectKeysJson);
        if (keys.Count > 0 && await TryDeleteObjectsAsync(keys, cancellationToken))
        {
            enrollment.PhotoObjectKeysJson = "[]";
            await _db.Updateable(enrollment)
                .UpdateColumns(e => new { e.PhotoObjectKeysJson })
                .ExecuteCommandAsync(cancellationToken);
        }

        return true;
    }

    // ---------- 辅助 ----------

    private void EnsureNswNoticeSufficient(DateTime? noticeDate)
    {
        var today = FaceMatchRules.LocalDate(timeProvider.GetUtcNow().UtcDateTime, StoreTimeZonePolicy.Sydney);
        if (noticeDate == null)
        {
            throw new FaceMatchException(409, "FACE_MATCH_NSW_NOTICE_REQUIRED", "该员工在 NSW 门店工作，登记前必须有书面通知日期。");
        }

        if (noticeDate.Value.Date > today.AddDays(-FaceMatchRules.NswNoticeMinDays))
        {
            throw new FaceMatchException(409, "FACE_MATCH_NSW_NOTICE_TOO_RECENT", "NSW 员工须在登记前至少 14 天收到书面通知，目前不满 14 天。");
        }
    }

    /// <summary>员工所在门店里有 NSW 门店的员工集合（门店时区 / 档案时区优先，其次邮编）。</summary>
    private async Task<HashSet<string>> FindNswUsersAsync(IReadOnlyCollection<string> userGuids, CancellationToken cancellationToken)
    {
        if (userGuids.Count == 0)
        {
            return [];
        }

        var rows = await _db.Queryable<UserStore>()
            .InnerJoin<Store>((link, store) => link.StoreGUID == store.StoreGUID)
            .Where((link, store) => userGuids.Contains(link.UserGUID) && !link.IsDeleted && !store.IsDeleted)
            .Select((link, store) => new { link.UserGUID, store.StoreCode, store.TimeZoneId, store.Address })
            .ToListAsync(cancellationToken);
        var codes = rows.Select(r => r.StoreCode).Distinct().ToList();
        var profileZones = codes.Count == 0
            ? new Dictionary<string, string?>()
            : (await _db.Queryable<FaceStoreProfile>().Where(p => codes.Contains(p.StoreCode)).ToListAsync(cancellationToken))
                .ToDictionary(p => p.StoreCode, p => p.TimeZoneId);

        return rows
            .Where(r => FaceMatchRules.ResolveJurisdiction(profileZones.GetValueOrDefault(r.StoreCode), r.TimeZoneId, r.Address) == "NSW")
            .Select(r => r.UserGUID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<FaceConsentRecord> RequireConsentAsync(string consentId, CancellationToken cancellationToken)
    {
        if (!FaceMatchObjectKeys.IsValidId(consentId))
        {
            throw FaceMatchErrors.NotFound("同意记录");
        }

        return await _db.Queryable<FaceConsentRecord>().FirstAsync(c => c.ConsentId == consentId, cancellationToken)
            ?? throw FaceMatchErrors.NotFound("同意记录");
    }

    private async Task PutAsync(string key, byte[] bytes, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            await objectStore.PutAsync(key, bytes, contentType, cancellationToken);
        }
        catch (FaceMatchStoreException ex)
        {
            throw FaceMatchErrors.FromStore(ex);
        }
    }

    private async Task RollbackEnrollmentAsync(string personId, bool personCreated, IReadOnlyList<string> uploadedKeys)
    {
        // 回滚尽力而为：不能让回滚失败盖掉原始错误；回滚不受调用方取消影响。
        if (personCreated)
        {
            try
            {
                await library.DeletePersonAsync(personId, CancellationToken.None);
            }
            catch (FaceLibraryException ex)
            {
                logger.LogError("登记失败后回滚腾讯云人员失败，需人工核对: PersonId={PersonId}, Code={Code}", personId, ex.Code);
            }
        }

        if (uploadedKeys.Count > 0)
        {
            await TryDeleteObjectsAsync(uploadedKeys, CancellationToken.None);
        }
    }

    /// <summary>逐个删除对象；全部成功返回 true。失败只记日志（不含对象键）。</summary>
    internal async Task<bool> TryDeleteObjectsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
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
                logger.LogWarning("人脸比对对象删除失败，稍后由后台任务重试: Kind={Kind}", ex.Kind);
            }
        }

        return allDeleted;
    }

    private static FaceConsentDto ToDto(FaceConsentRecord r, string? userName) => new()
    {
        ConsentId = r.ConsentId,
        UserGuid = r.UserGuid,
        UserName = userName,
        ConsentVersion = r.ConsentVersion,
        SignedAtUtc = FaceMatchRules.AsUtc(r.SignedAtUtc),
        Method = r.Method,
        HasEvidence = !string.IsNullOrEmpty(r.EvidenceObjectKey),
        NswNoticeDate = FaceMatchRules.FormatDate(r.NswNoticeDate),
        WithdrawnAtUtc = FaceMatchRules.AsUtc(r.WithdrawnAtUtc),
        WithdrawnReason = r.WithdrawnReason,
        CreatedAtUtc = FaceMatchRules.AsUtc(r.CreatedAtUtc),
        CreatedBy = r.CreatedBy,
        Active = r.WithdrawnAtUtc == null,
    };

    private static FaceEnrollmentDto ToDto(FaceEnrollment r, string? userName, bool consentWithdrawn) => new()
    {
        EnrollmentId = r.EnrollmentId,
        UserGuid = r.UserGuid,
        UserName = userName,
        ConsentId = r.ConsentId,
        PhotoCount = FaceMatchRules.ParseKeys(r.PhotoObjectKeysJson).Count,
        Status = r.Status,
        StatusName = r.Status == FaceEnrollmentStatus.Active ? "active" : "withdrawn",
        EnrolledAtUtc = FaceMatchRules.AsUtc(r.EnrolledAtUtc),
        EnrolledBy = r.EnrolledBy,
        WithdrawnAtUtc = FaceMatchRules.AsUtc(r.WithdrawnAtUtc),
        PendingRemoteDeletion = r.Status == FaceEnrollmentStatus.Active && consentWithdrawn,
    };
}
