using BlazorApp.Api.Data;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 门店人脸比对档案与启用闸门（设计 13.3、13.4、14.1、8.5）。闸门分两类，都在后端计算：
/// <para><b>阻塞（Missing，缺了保存返回 409 FACE_MATCH_GATE_NOT_MET）</b>——结构性 / 功能性前提，不是合规记录：
/// 1. Mode=Capture 必填 AllowedSubjects（只拍知情的内部人员）；EvaluateMatch 只允许在 Capture 下；
/// 2. 员工排除 / 去重 / 名单三个开关都要求模式不是 Off；
/// 3. StaffExclusionEnabled 需门店成员里存在有效的同意与登记。</para>
/// <para><b>警告（Warnings，不阻塞保存与启用，但必须始终可见）</b>——合规记录缺口。2026-10-13 用户明确决定
/// 「Match 全部闸门改成警告，不再拦截」，风险由用户承担；系统不替代法律判断，对真实顾客（Match）运行前法律上仍须补齐：
/// 4. Mode=Match：PIA、告示日期、律师确认、跨境披露审查（地域须等于当前配置的处理地域）；NSW 门店另需满 14 天的员工通知日期；
/// 5. Match 下的顾客去重：去重专项律师确认；
/// 6. Match 下的不受欢迎人员名单：专项评估 / 律师确认 / PIA / 告示 / 培训 / 隐私联系人 + 跨境披露审查。
///    Capture 下（只处理知情的内部人员）这些记录本来就不要求，所以也没有警告；切到 Match 时重新计算。</para>
/// 各 facet 的构造是不依赖数据库与时钟的静态纯函数（<see cref="BuildReport"/>），详情 / 检查 / 保存 / 列表共用同一套口径。
/// 保存成功后若带警告，审计 Result 记 <c>mode=N;warnings=M</c>，留下「带着缺口启用」的痕迹。
/// 档案读取失败时接收端按 Off 处理。
/// </summary>
public sealed class FaceStoreProfileService(
    SqlSugarContext context,
    IFaceMatchSettingsProvider settingsProvider,
    IFaceMatchObjectStore objectStore,
    IFaceMatchAudit audit,
    TimeProvider timeProvider
) : IFaceStoreProfileService
{
    private readonly ISqlSugarClient _db = context.Db;

    public Task<FaceMatchConfigDto> GetConfigAsync(FaceMatchPermissionFlagsDto permissions, CancellationToken cancellationToken = default)
    {
        var resolved = settingsProvider.Resolved;
        var options = settingsProvider.Options;
        return Task.FromResult(new FaceMatchConfigDto
        {
            Enabled = resolved.Enabled,
            DisabledReason = resolved.DisabledReason,
            Region = resolved.Enabled ? resolved.Region : null,
            AllowedRegions = FaceMatchSettings.AllowedRegions,
            ObjectStoreConfigured = objectStore.IsConfigured,
            HighBandScore = options.Unwelcome.HighBandScore,
            CaptureRetentionDays = options.CaptureRetentionDays >= 1 ? options.CaptureRetentionDays : 7,
            Permissions = permissions,
        });
    }

    public async Task<IReadOnlyList<FaceStoreProfileListItemDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _db.Queryable<FaceStoreProfile>().OrderBy(p => p.StoreCode).ToListAsync(cancellationToken);
        if (profiles.Count == 0)
        {
            return [];
        }

        var codes = profiles.Select(p => p.StoreCode).ToList();
        // 门店名称之外顺带取时区与地址：算「合规记录警告数」要判断是否 NSW 门店（NSW 另需员工通知日期）。
        var stores = (await _db.Queryable<Store>()
                .Where(s => codes.Contains(s.StoreCode) && !s.IsDeleted)
                .Select(s => new { s.StoreCode, s.StoreName, s.TimeZoneId, s.Address })
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.StoreCode)
            .ToDictionary(g => g.Key, g => g.First());

        var resolved = settingsProvider.Resolved;
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        return profiles.Select(p =>
        {
            stores.TryGetValue(p.StoreCode, out var store);
            // 与 GetAsync 同一套纯函数；员工登记数传 0——列表只关心警告，不关心「阻塞缺项」（已保存的档案已通过阻塞校验）。
            var draft = DraftFromEntity(p);
            var timeZoneId = FaceMatchRules.ResolveTimeZoneId(draft.TimeZoneId, store?.TimeZoneId, store?.Address);
            var isNsw = FaceMatchRules.ResolveJurisdiction(draft.TimeZoneId, store?.TimeZoneId, store?.Address) == "NSW";
            var report = BuildReport(draft, resolved, isNsw, FaceMatchRules.LocalDate(nowUtc, timeZoneId), activeEnrollments: 0);
            return new FaceStoreProfileListItemDto
            {
                StoreCode = p.StoreCode,
                StoreName = store?.StoreName,
                Mode = p.Mode,
                EvaluateMatch = p.EvaluateMatch,
                StaffExclusionEnabled = p.StaffExclusionEnabled,
                TrafficDedupEnabled = p.TrafficDedupEnabled,
                UnwelcomeListEnabled = p.UnwelcomeListEnabled,
                CrossBorderReviewValid = IsCrossBorderReviewValid(p, resolved),
                GateWarningCount = CountWarnings(report),
                UpdatedAtUtc = FaceMatchRules.AsUtc(p.UpdatedAtUtc),
            };
        }).ToList();
    }

    public async Task<FaceStoreProfileDto> GetAsync(string storeCode, CancellationToken cancellationToken = default)
    {
        var store = await RequireStoreAsync(storeCode, cancellationToken);
        var profile = await _db.Queryable<FaceStoreProfile>().FirstAsync(p => p.StoreCode == store.StoreCode, cancellationToken);
        var draft = profile != null ? DraftFromEntity(profile) : new ProfileDraft();
        var report = await EvaluateAsync(store, draft, cancellationToken);
        return ToDto(store, profile, report);
    }

    public async Task<FaceGateReportDto> CheckAsync(
        string storeCode, FaceStoreProfileUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var store = await RequireStoreAsync(storeCode, cancellationToken);
        return await EvaluateAsync(store, Normalize(request), cancellationToken);
    }

    public async Task<FaceStoreProfileDto> UpdateAsync(
        string storeCode, FaceStoreProfileUpdateRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var store = await RequireStoreAsync(storeCode, cancellationToken);
        var draft = Normalize(request);
        var report = await EvaluateAsync(store, draft, cancellationToken);
        if (!report.CanSave)
        {
            await audit.WriteAsync(actor, FaceMatchAuditActions.ProfileUpdate, store.StoreCode, store.StoreCode, "gate-not-met", cancellationToken);
            throw new FaceMatchException(409, "FACE_MATCH_GATE_NOT_MET", "启用前置条件不满足，请补全后再保存。", report);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = ToEntity(store.StoreCode, draft, now, actor.Name);
        var exists = await _db.Queryable<FaceStoreProfile>().AnyAsync(p => p.StoreCode == store.StoreCode, cancellationToken);
        if (exists)
        {
            await _db.Updateable(entity).ExecuteCommandAsync(cancellationToken);
        }
        else
        {
            try
            {
                await _db.Insertable(entity).ExecuteCommandAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 并发首次保存：另一个请求刚插入了同一行，改为更新（最后写入者生效）；真的写不进去则原样抛出。
                if (!await _db.Queryable<FaceStoreProfile>().AnyAsync(p => p.StoreCode == store.StoreCode, cancellationToken))
                {
                    throw;
                }

                await _db.Updateable(entity).ExecuteCommandAsync(cancellationToken);
            }
        }

        // 带着合规记录缺口保存（Match 闸门已改为警告）要在审计里留痕：Result 列最长 32 位，
        // "mode=2;warnings=NN" 最多 18 位；无警告时保持原来的 "mode=N"。
        var auditResult = report.HasWarnings
            ? $"mode={draft.Mode};warnings={CountWarnings(report)}"
            : $"mode={draft.Mode}";
        await audit.WriteAsync(
            actor, FaceMatchAuditActions.ProfileUpdate, store.StoreCode, store.StoreCode,
            auditResult, cancellationToken);
        return ToDto(store, entity, report);
    }

    // ---------- 校验与规范化 ----------

    private static FaceMatchException FieldInvalid(string field, string message) =>
        new(400, "FACE_MATCH_FIELD_INVALID", message, new { field });

    /// <summary>把请求规范化成草稿：trim、空串转 null，格式 / 长度 / 取值非法抛 400（与闸门缺项是两回事）。</summary>
    private ProfileDraft Normalize(FaceStoreProfileUpdateRequest request)
    {
        if (request.Mode is not int mode || !FaceStoreMode.IsValid(mode))
        {
            throw FieldInvalid("mode", "模式只能是 0（关闭）、1（拍摄留存）或 2（匹配）。");
        }

        string? Text(string? value, int max, string field)
        {
            if (!FaceMatchRules.TryNormalizeText(value, max, out var normalized))
            {
                throw FieldInvalid(field, $"{field} 过长或含非法字符（最长 {max}）。");
            }

            return normalized;
        }

        DateTime? Date(string? value, string field, bool allowFuture)
        {
            if (!FaceMatchRules.TryParseDate(value, out var parsed))
            {
                throw FieldInvalid(field, $"{field} 必须是 yyyy-MM-dd 格式的日期。");
            }

            if (parsed.HasValue && !allowFuture && parsed.Value > Today(null))
            {
                throw FieldInvalid(field, $"{field} 不能晚于今天。");
            }

            return parsed;
        }

        DateTime? Stamp(DateTime? value, string field)
        {
            if (value.HasValue && value.Value.ToUniversalTime() > timeProvider.GetUtcNow().UtcDateTime.AddDays(1))
            {
                throw FieldInvalid(field, $"{field} 不能晚于当前时间。");
            }

            return value.HasValue ? DateTime.SpecifyKind(value.Value.ToUniversalTime(), DateTimeKind.Utc) : null;
        }

        if (!StoreTimeZonePolicy.TryNormalize(request.TimeZoneId, out var timeZone))
        {
            throw FieldInvalid("timeZoneId", "时区只允许 Australia/Brisbane、Australia/Sydney 或 Australia/Melbourne。");
        }

        var region = Text(request.CrossBorderRegion, 32, "crossBorderRegion");
        if (region != null && !FaceMatchSettings.AllowedRegions.Contains(region, StringComparer.Ordinal))
        {
            throw FieldInvalid("crossBorderRegion", "处理地域不在允许的名单内。");
        }

        return new ProfileDraft
        {
            Mode = (byte)mode,
            EvaluateMatch = request.EvaluateMatch,
            StaffExclusionEnabled = request.StaffExclusionEnabled,
            TrafficDedupEnabled = request.TrafficDedupEnabled,
            UnwelcomeListEnabled = request.UnwelcomeListEnabled,
            AllowedSubjects = Text(request.AllowedSubjects, 200, "allowedSubjects"),
            PiaReference = Text(request.PiaReference, 100, "piaReference"),
            SignageInstalledOn = Date(request.SignageInstalledOn, "signageInstalledOn", allowFuture: false),
            StaffNoticeDate = Date(request.StaffNoticeDate, "staffNoticeDate", allowFuture: false),
            LegalSignoffBy = Text(request.LegalSignoffBy, 100, "legalSignoffBy"),
            LegalSignoffAt = Stamp(request.LegalSignoffAt, "legalSignoffAt"),
            DedupLegalSignoffBy = Text(request.DedupLegalSignoffBy, 100, "dedupLegalSignoffBy"),
            DedupLegalSignoffAt = Stamp(request.DedupLegalSignoffAt, "dedupLegalSignoffAt"),
            CrossBorderRegion = region,
            CrossBorderReviewRef = Text(request.CrossBorderReviewRef, 100, "crossBorderReviewRef"),
            CrossBorderReviewedBy = Text(request.CrossBorderReviewedBy, 100, "crossBorderReviewedBy"),
            CrossBorderReviewedAt = Stamp(request.CrossBorderReviewedAt, "crossBorderReviewedAt"),
            TimeZoneId = timeZone,
            UnwelcomeAssessmentRef = Text(request.UnwelcomeAssessmentRef, 100, "unwelcomeAssessmentRef"),
            UnwelcomeLegalSignoffBy = Text(request.UnwelcomeLegalSignoffBy, 100, "unwelcomeLegalSignoffBy"),
            UnwelcomeLegalSignoffAt = Stamp(request.UnwelcomeLegalSignoffAt, "unwelcomeLegalSignoffAt"),
            StaffTrainingDate = Date(request.StaffTrainingDate, "staffTrainingDate", allowFuture: false),
            PrivacyContact = Text(request.PrivacyContact, 100, "privacyContact"),
        };
    }

    /// <summary>「今天」按门店时区算；未知门店时区时按悉尼（只用于「不能晚于今天」这类宽松校验）。</summary>
    private DateTime Today(string? timeZoneId) =>
        FaceMatchRules.LocalDate(
            timeProvider.GetUtcNow().UtcDateTime,
            timeZoneId ?? StoreTimeZonePolicy.Sydney);

    // ---------- 闸门 ----------

    private async Task<FaceGateReportDto> EvaluateAsync(FaceMatchStoreInfo store, ProfileDraft draft, CancellationToken cancellationToken)
    {
        var resolved = settingsProvider.Resolved;
        var jurisdiction = FaceMatchRules.ResolveJurisdiction(draft.TimeZoneId, store.TimeZoneId, store.Address);
        var timeZoneId = FaceMatchRules.ResolveTimeZoneId(draft.TimeZoneId, store.TimeZoneId, store.Address);
        var today = FaceMatchRules.LocalDate(timeProvider.GetUtcNow().UtcDateTime, timeZoneId);

        var needsEnrollmentCheck = draft.StaffExclusionEnabled;
        var activeEnrollments = needsEnrollmentCheck ? await CountActiveEnrollmentsAsync(store.StoreCode, cancellationToken) : 0;

        return BuildReport(draft, resolved, jurisdiction == "NSW", today, activeEnrollments);
    }

    /// <summary>
    /// 把五个 facet 汇总成闸门报告（纯函数：不碰数据库与时钟，入参全部由调用方给）。
    /// CanSave 只看 Missing（仍阻塞的项）；HasWarnings 看 Warnings（合规记录缺口，不阻塞）。
    /// </summary>
    private static FaceGateReportDto BuildReport(
        ProfileDraft draft, FaceMatchResolvedSettings resolved, bool isNsw, DateTime today, int activeEnrollments)
    {
        var facets = new List<FaceGateFacetDto>
        {
            CaptureFacet(draft),
            MatchFacet(draft, resolved, isNsw, today),
            StaffFacet(draft, activeEnrollments),
            DedupFacet(draft),
            UnwelcomeFacet(draft, resolved),
        };
        return new FaceGateReportDto
        {
            Facets = facets,
            CanSave = facets.All(f => f.Missing.Count == 0),
            HasWarnings = facets.Any(f => f.Warnings.Count > 0),
        };
    }

    /// <summary>各 facet 警告条数之和（同一字段被多个 facet 要求时分别计数，与页面各 facet 提示的条数相加一致）。</summary>
    private static int CountWarnings(FaceGateReportDto report) => report.Facets.Sum(f => f.Warnings.Count);

    /// <summary>阻塞：Capture 必须声明只拍知情的内部人员；评估匹配只允许在 Capture 下。</summary>
    private static FaceGateFacetDto CaptureFacet(ProfileDraft draft)
    {
        var requested = draft.Mode == FaceStoreMode.Capture || draft.EvaluateMatch;
        var missing = new List<string>();
        if (requested)
        {
            if (draft.Mode == FaceStoreMode.Capture && string.IsNullOrEmpty(draft.AllowedSubjects))
            {
                missing.Add("allowedSubjects");
            }

            if (draft.EvaluateMatch && draft.Mode != FaceStoreMode.Capture)
            {
                missing.Add("evaluateMatchRequiresCapture");
            }
        }

        return new FaceGateFacetDto { Facet = "capture", Requested = requested, Missing = missing };
    }

    /// <summary>Match 模式的合规记录：全部是警告（不阻塞）。</summary>
    private static FaceGateFacetDto MatchFacet(ProfileDraft draft, FaceMatchResolvedSettings resolved, bool isNsw, DateTime today)
    {
        var requested = draft.Mode == FaceStoreMode.Match;
        var warnings = new List<string>();
        if (requested)
        {
            AddIfBlank(warnings, "piaReference", draft.PiaReference);
            if (draft.SignageInstalledOn == null) warnings.Add("signageInstalledOn");
            AddIfBlank(warnings, "legalSignoffBy", draft.LegalSignoffBy);
            if (draft.LegalSignoffAt == null) warnings.Add("legalSignoffAt");
            if (isNsw)
            {
                if (draft.StaffNoticeDate == null)
                {
                    warnings.Add("staffNoticeDate");
                }
                else if (draft.StaffNoticeDate.Value > today.AddDays(-FaceMatchRules.NswNoticeMinDays))
                {
                    // NSW 工作场所监控法：员工须提前至少 14 天书面通知（条文原文未核实，【待律师确认】）。
                    warnings.Add("staffNoticeTooRecent");
                }
            }

            AddCrossBorderWarnings(warnings, draft, resolved);
        }

        return new FaceGateFacetDto { Facet = "match", Requested = requested, Warnings = warnings };
    }

    /// <summary>阻塞：员工排除要求模式不是 Off，且该店至少一份有效登记（功能前提，不是法律记录）。</summary>
    private static FaceGateFacetDto StaffFacet(ProfileDraft draft, int activeEnrollments)
    {
        var requested = draft.StaffExclusionEnabled;
        var missing = new List<string>();
        if (requested)
        {
            if (draft.Mode == FaceStoreMode.Off) missing.Add("modeOff");
            if (activeEnrollments == 0) missing.Add("activeEnrollment");
        }

        return new FaceGateFacetDto { Facet = "staffExclusion", Requested = requested, Missing = missing };
    }

    /// <summary>阻塞：去重要求模式不是 Off。Match 下的去重专项律师确认是警告。</summary>
    private static FaceGateFacetDto DedupFacet(ProfileDraft draft)
    {
        var requested = draft.TrafficDedupEnabled;
        var missing = new List<string>();
        var warnings = new List<string>();
        if (requested)
        {
            if (draft.Mode == FaceStoreMode.Off) missing.Add("modeOff");
            // 对真实顾客（Match）运行前须有去重专项律师确认——现在只是警告；Capture 下仅内部知情人员验证，不要求也不警告。
            if (draft.Mode == FaceStoreMode.Match)
            {
                AddIfBlank(warnings, "dedupLegalSignoffBy", draft.DedupLegalSignoffBy);
                if (draft.DedupLegalSignoffAt == null) warnings.Add("dedupLegalSignoffAt");
            }
        }

        return new FaceGateFacetDto { Facet = "trafficDedup", Requested = requested, Missing = missing, Warnings = warnings };
    }

    /// <summary>阻塞：名单要求模式不是 Off。Match 下的专项记录全部是警告；Capture 下不要求。</summary>
    private static FaceGateFacetDto UnwelcomeFacet(ProfileDraft draft, FaceMatchResolvedSettings resolved)
    {
        var requested = draft.UnwelcomeListEnabled;
        var missing = new List<string>();
        var warnings = new List<string>();
        if (requested)
        {
            if (draft.Mode == FaceStoreMode.Off) missing.Add("modeOff");
            // 对真实顾客（Match）运行前才需要专项记录；Capture 下只处理知情的内部人员（AllowedSubjects 由 capture 闸门强制），
            // 名单参考照片与命中图都只涉及内部人员，与顾客去重同一原则：不要求（也不警告）专项评估、律师确认、PIA、告示、培训、
            // 隐私联系人和跨境审查，用于上线前用内部员工的照片端到端测试名单。切到 Match 时这些记录重新计算，缺了显示为警告。
            if (draft.Mode == FaceStoreMode.Match)
            {
                AddIfBlank(warnings, "unwelcomeAssessmentRef", draft.UnwelcomeAssessmentRef);
                AddIfBlank(warnings, "unwelcomeLegalSignoffBy", draft.UnwelcomeLegalSignoffBy);
                if (draft.UnwelcomeLegalSignoffAt == null) warnings.Add("unwelcomeLegalSignoffAt");
                AddIfBlank(warnings, "piaReference", draft.PiaReference);
                if (draft.SignageInstalledOn == null) warnings.Add("signageInstalledOn");
                if (draft.StaffTrainingDate == null) warnings.Add("staffTrainingDate");
                AddIfBlank(warnings, "privacyContact", draft.PrivacyContact);
                // Match 下名单要求跨境披露审查（名单照片与每张送检人脸都发往境外）。
                AddCrossBorderWarnings(warnings, draft, resolved);
            }
        }

        return new FaceGateFacetDto { Facet = "unwelcome", Requested = requested, Missing = missing, Warnings = warnings };
    }

    private static void AddCrossBorderWarnings(List<string> warnings, ProfileDraft draft, FaceMatchResolvedSettings resolved)
    {
        if (string.IsNullOrEmpty(draft.CrossBorderRegion))
        {
            AddOnce(warnings, "crossBorderRegion");
        }
        else if (!resolved.Enabled || !string.Equals(draft.CrossBorderRegion, resolved.Region, StringComparison.Ordinal))
        {
            // 审查记录里的地域必须等于当前配置的处理地域；功能未开启（没有有效地域）时同样算缺口。
            AddOnce(warnings, "regionMismatch");
        }

        AddIfBlank(warnings, "crossBorderReviewRef", draft.CrossBorderReviewRef, once: true);
        AddIfBlank(warnings, "crossBorderReviewedBy", draft.CrossBorderReviewedBy, once: true);
        if (draft.CrossBorderReviewedAt == null) AddOnce(warnings, "crossBorderReviewedAt");
    }

    private static void AddIfBlank(List<string> list, string key, string? value, bool once = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (once) AddOnce(list, key); else list.Add(key);
        }
    }

    private static void AddOnce(List<string> list, string key)
    {
        if (!list.Contains(key)) list.Add(key);
    }

    /// <summary>门店成员里有效登记（Active 且同意未撤回）的人数。</summary>
    private async Task<int> CountActiveEnrollmentsAsync(string storeCode, CancellationToken cancellationToken)
    {
        return await _db.Queryable<FaceEnrollment>()
            .InnerJoin<FaceConsentRecord>((e, c) => e.ConsentId == c.ConsentId)
            .InnerJoin<UserStore>((e, c, us) => e.UserGuid == us.UserGUID)
            .InnerJoin<Store>((e, c, us, s) => us.StoreGUID == s.StoreGUID)
            .Where((e, c, us, s) =>
                e.Status == FaceEnrollmentStatus.Active
                && c.WithdrawnAtUtc == null
                && !us.IsDeleted
                && !s.IsDeleted
                && s.StoreCode == storeCode)
            .Select((e, c, us, s) => e.EnrollmentId)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    // ---------- 映射 ----------

    private async Task<FaceMatchStoreInfo> RequireStoreAsync(string storeCode, CancellationToken cancellationToken)
    {
        var code = storeCode?.Trim();
        if (!BlazorApp.Api.Features.FaceCameras.FaceCameraRules.IsValidStoreCode(code))
        {
            throw new FaceMatchException(400, "FACE_MATCH_STORE_INVALID", "门店编码格式不正确。");
        }

        return await FaceMatchStoreInfo.FindAsync(_db, code!, cancellationToken)
            ?? throw new FaceMatchException(404, "FACE_MATCH_STORE_NOT_FOUND", "门店不存在或已停用。");
    }

    private static bool IsCrossBorderReviewValid(FaceStoreProfile profile, FaceMatchResolvedSettings resolved) =>
        resolved.Enabled
        && !string.IsNullOrEmpty(profile.CrossBorderRegion)
        && string.Equals(profile.CrossBorderRegion, resolved.Region, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(profile.CrossBorderReviewRef)
        && !string.IsNullOrWhiteSpace(profile.CrossBorderReviewedBy)
        && profile.CrossBorderReviewedAt != null;

    private static ProfileDraft DraftFromEntity(FaceStoreProfile p) => new()
    {
        Mode = p.Mode,
        EvaluateMatch = p.EvaluateMatch,
        StaffExclusionEnabled = p.StaffExclusionEnabled,
        TrafficDedupEnabled = p.TrafficDedupEnabled,
        UnwelcomeListEnabled = p.UnwelcomeListEnabled,
        AllowedSubjects = p.AllowedSubjects,
        PiaReference = p.PiaReference,
        SignageInstalledOn = p.SignageInstalledOn?.Date,
        StaffNoticeDate = p.StaffNoticeDate?.Date,
        LegalSignoffBy = p.LegalSignoffBy,
        LegalSignoffAt = p.LegalSignoffAt,
        DedupLegalSignoffBy = p.DedupLegalSignoffBy,
        DedupLegalSignoffAt = p.DedupLegalSignoffAt,
        CrossBorderRegion = p.CrossBorderRegion,
        CrossBorderReviewRef = p.CrossBorderReviewRef,
        CrossBorderReviewedBy = p.CrossBorderReviewedBy,
        CrossBorderReviewedAt = p.CrossBorderReviewedAt,
        TimeZoneId = p.TimeZoneId,
        UnwelcomeAssessmentRef = p.UnwelcomeAssessmentRef,
        UnwelcomeLegalSignoffBy = p.UnwelcomeLegalSignoffBy,
        UnwelcomeLegalSignoffAt = p.UnwelcomeLegalSignoffAt,
        StaffTrainingDate = p.StaffTrainingDate?.Date,
        PrivacyContact = p.PrivacyContact,
    };

    private static FaceStoreProfile ToEntity(string storeCode, ProfileDraft d, DateTime now, string updatedBy) => new()
    {
        StoreCode = storeCode,
        Mode = d.Mode,
        EvaluateMatch = d.EvaluateMatch,
        StaffExclusionEnabled = d.StaffExclusionEnabled,
        TrafficDedupEnabled = d.TrafficDedupEnabled,
        UnwelcomeListEnabled = d.UnwelcomeListEnabled,
        AllowedSubjects = d.AllowedSubjects,
        PiaReference = d.PiaReference,
        SignageInstalledOn = d.SignageInstalledOn,
        StaffNoticeDate = d.StaffNoticeDate,
        LegalSignoffBy = d.LegalSignoffBy,
        LegalSignoffAt = d.LegalSignoffAt,
        DedupLegalSignoffBy = d.DedupLegalSignoffBy,
        DedupLegalSignoffAt = d.DedupLegalSignoffAt,
        CrossBorderRegion = d.CrossBorderRegion,
        CrossBorderReviewRef = d.CrossBorderReviewRef,
        CrossBorderReviewedBy = d.CrossBorderReviewedBy,
        CrossBorderReviewedAt = d.CrossBorderReviewedAt,
        TimeZoneId = d.TimeZoneId,
        UnwelcomeAssessmentRef = d.UnwelcomeAssessmentRef,
        UnwelcomeLegalSignoffBy = d.UnwelcomeLegalSignoffBy,
        UnwelcomeLegalSignoffAt = d.UnwelcomeLegalSignoffAt,
        StaffTrainingDate = d.StaffTrainingDate,
        PrivacyContact = d.PrivacyContact,
        UpdatedAtUtc = now,
        UpdatedBy = FaceMatchAudit.Sanitize(updatedBy, 100),
    };

    private FaceStoreProfileDto ToDto(FaceMatchStoreInfo store, FaceStoreProfile? p, FaceGateReportDto gates)
    {
        var resolved = settingsProvider.Resolved;
        var e = p ?? new FaceStoreProfile { StoreCode = store.StoreCode };
        return new FaceStoreProfileDto
        {
            StoreCode = store.StoreCode,
            StoreName = store.StoreName,
            Exists = p != null,
            Mode = e.Mode,
            EvaluateMatch = e.EvaluateMatch,
            StaffExclusionEnabled = e.StaffExclusionEnabled,
            TrafficDedupEnabled = e.TrafficDedupEnabled,
            UnwelcomeListEnabled = e.UnwelcomeListEnabled,
            AllowedSubjects = e.AllowedSubjects,
            PiaReference = e.PiaReference,
            SignageInstalledOn = FaceMatchRules.FormatDate(e.SignageInstalledOn),
            StaffNoticeDate = FaceMatchRules.FormatDate(e.StaffNoticeDate),
            LegalSignoffBy = e.LegalSignoffBy,
            LegalSignoffAt = FaceMatchRules.AsUtc(e.LegalSignoffAt),
            DedupLegalSignoffBy = e.DedupLegalSignoffBy,
            DedupLegalSignoffAt = FaceMatchRules.AsUtc(e.DedupLegalSignoffAt),
            CrossBorderRegion = e.CrossBorderRegion,
            CrossBorderReviewRef = e.CrossBorderReviewRef,
            CrossBorderReviewedBy = e.CrossBorderReviewedBy,
            CrossBorderReviewedAt = FaceMatchRules.AsUtc(e.CrossBorderReviewedAt),
            TimeZoneId = e.TimeZoneId,
            UnwelcomeAssessmentRef = e.UnwelcomeAssessmentRef,
            UnwelcomeLegalSignoffBy = e.UnwelcomeLegalSignoffBy,
            UnwelcomeLegalSignoffAt = FaceMatchRules.AsUtc(e.UnwelcomeLegalSignoffAt),
            StaffTrainingDate = FaceMatchRules.FormatDate(e.StaffTrainingDate),
            PrivacyContact = e.PrivacyContact,
            UpdatedAtUtc = p != null ? FaceMatchRules.AsUtc(p.UpdatedAtUtc) : null,
            UpdatedBy = e.UpdatedBy,
            EffectiveTimeZoneId = FaceMatchRules.ResolveTimeZoneId(e.TimeZoneId, store.TimeZoneId, store.Address),
            Jurisdiction = FaceMatchRules.ResolveJurisdiction(e.TimeZoneId, store.TimeZoneId, store.Address),
            CrossBorderReviewValid = p != null && IsCrossBorderReviewValid(p, resolved),
            Gates = gates,
        };
    }

    /// <summary>规范化后的档案草稿（与实体字段一一对应，日期已是仅日期部分）。</summary>
    private sealed class ProfileDraft
    {
        public byte Mode { get; init; }
        public bool EvaluateMatch { get; init; }
        public bool StaffExclusionEnabled { get; init; }
        public bool TrafficDedupEnabled { get; init; }
        public bool UnwelcomeListEnabled { get; init; }
        public string? AllowedSubjects { get; init; }
        public string? PiaReference { get; init; }
        public DateTime? SignageInstalledOn { get; init; }
        public DateTime? StaffNoticeDate { get; init; }
        public string? LegalSignoffBy { get; init; }
        public DateTime? LegalSignoffAt { get; init; }
        public string? DedupLegalSignoffBy { get; init; }
        public DateTime? DedupLegalSignoffAt { get; init; }
        public string? CrossBorderRegion { get; init; }
        public string? CrossBorderReviewRef { get; init; }
        public string? CrossBorderReviewedBy { get; init; }
        public DateTime? CrossBorderReviewedAt { get; init; }
        public string? TimeZoneId { get; init; }
        public string? UnwelcomeAssessmentRef { get; init; }
        public string? UnwelcomeLegalSignoffBy { get; init; }
        public DateTime? UnwelcomeLegalSignoffAt { get; init; }
        public DateTime? StaffTrainingDate { get; init; }
        public string? PrivacyContact { get; init; }
    }
}
