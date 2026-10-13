namespace BlazorApp.FaceIngest;

/// <summary>门店有效处理模式（设计第 13.5 节）。</summary>
internal enum StoreModeKind
{
    /// <summary>不调用腾讯云，也不留生物数据：处理器读完即删本地报文，不存图、不写事件行。</summary>
    Off,

    /// <summary>现有 P1 行为完全不变（全部留存）；可选再做匹配评估写 FaceMatchEvaluation。</summary>
    Capture,

    /// <summary>匹配后丢弃：质量闸门 → 一次搜索 → 只累加计数 → 删除全部本地报文，不上传 COS（名单命中图除外）。</summary>
    Match,
}

/// <summary>
/// 某个门店此刻实际生效的处理模式与开关，已把档案、地域闸门、配置可用性全部折算进去：
/// 下游（处理器、流水线）只看这个对象，不再自己解释档案。
/// </summary>
internal sealed record EffectiveStoreMode(
    StoreModeKind Kind,
    bool Evaluate,
    bool StaffExclusion,
    bool TrafficDedup,
    bool Unwelcome,
    string? TimeZoneId,
    string Reason)
{
    /// <summary>未启用任何档案机制时的现状行为：等同 Capture，不评估。</summary>
    public static EffectiveStoreMode Legacy { get; } = new(StoreModeKind.Capture, false, false, false, false, null, "legacy");

    public static EffectiveStoreMode Off(string reason) => new(StoreModeKind.Off, false, false, false, false, null, reason);
}

/// <summary>
/// 把门店档案折算成有效模式。业务口径：
/// 1. 没配数据库 → 无档案来源 → 现状（Capture）。P0 / cameras.json 环境完全不受影响；
/// 2. 配了数据库：该店有档案 → 按档案；<b>读取从未成功或没有该店档案</b> → 若已配置腾讯云凭据则 Off
///    （宁可不识别，也不在读不到配置时放开留存），若完全没配凭据（功能未启用）则保持现状，
///    这样新代码上线、迁移 / 授权 / 凭据都没就绪时不改变 1042 等现有门店的行为；
/// 3. <c>Mode=Match</c> 但匹配不可用（缺凭据 / 地域非法）→ Off，绝不退回「全部留存」；
/// 4. <c>Mode=Match</c> 且启用了员工排除 / 去重 / 名单，要求档案的 CrossBorderRegion 等于当前配置地域
///    且有跨境审查记录（地域改动后旧审查作废），否则降级：AllowedSubjects 非空 → Capture，否则 → Off，并记 Warning；
/// 5. 名单只在 COS 可用（命中图要存私有 COS）且名单专项记录齐全时才生效，否则关掉名单这一块并记 Warning；
/// 6. 去重需要能解析门店时区（算营业日与访客库删除时间），解析不了就关掉去重并记 Warning。
/// </summary>
internal sealed class StoreModePolicy
{
    private static readonly TimeSpan WarningLogWindow = TimeSpan.FromMinutes(10);

    private readonly FaceIngestSettingsMonitor _ingestSettings;
    private readonly FaceMatchSettings _matchSettings;
    private readonly FaceStoreProfileSource? _source;
    private readonly IUnwelcomeHitImageStore _hitImageStore;
    private readonly ILogger<StoreModePolicy> _logger;
    private readonly ThrottledLogGate _warningGate;

    public StoreModePolicy(
        FaceIngestSettingsMonitor ingestSettings,
        FaceMatchSettings matchSettings,
        IServiceProvider services,
        IUnwelcomeHitImageStore hitImageStore,
        TimeProvider timeProvider,
        ILogger<StoreModePolicy> logger)
    {
        _ingestSettings = ingestSettings;
        _matchSettings = matchSettings;
        _hitImageStore = hitImageStore;
        _logger = logger;
        _warningGate = new ThrottledLogGate(timeProvider, WarningLogWindow);
        // 中文注释：只有配了数据库才有档案来源（来源的仓储需要连接串，没配时不能去解析它）。
        _source = ingestSettings.Current.DatabaseConfigured ? services.GetRequiredService<FaceStoreProfileSource>() : null;
    }

    /// <summary>
    /// 启动瞬间档案还没尝试读过，而功能已配置时，处理器应先等一轮（下一轮 5 秒后再来），
    /// 避免把「还没来得及读」误当成「读不到」而把抓拍丢掉。读失败过一次后不再等（按 Off，符合规格）。
    /// </summary>
    public bool ShouldDeferProcessing => _source is not null && _matchSettings.CredentialsConfigured && !_source.HasAttempted;

    /// <summary>解析某门店当前的有效模式。线程安全，无 IO。</summary>
    public EffectiveStoreMode Resolve(string storeCode)
    {
        if (_source is null)
        {
            return EffectiveStoreMode.Legacy;
        }

        var snapshot = _source.Snapshot;
        if (snapshot is null || !snapshot.TryGetValue(storeCode, out var profile))
        {
            // 中文注释：读不到 / 没档案：功能已配置则 Off；完全未配置则保持现状（见类注释第 2 点）。
            if (!_matchSettings.CredentialsConfigured)
            {
                return EffectiveStoreMode.Legacy;
            }

            return EffectiveStoreMode.Off(snapshot is null ? "profiles-unavailable" : "no-profile");
        }

        return profile.Mode switch
        {
            0 => EffectiveStoreMode.Off("profile-off"),
            1 => ResolveCapture(profile),
            2 => ResolveMatch(profile),
            _ => WarnAndOff(profile, "unknown-mode"),
        };
    }

    private EffectiveStoreMode ResolveCapture(FaceStoreProfileRow profile)
    {
        // 评估需要匹配可用，且至少有一个要评估的库（员工库 / 当日访客库）。
        var evaluate = profile.EvaluateMatch
            && _matchSettings.MatcherEnabled
            && (profile.StaffExclusionEnabled || profile.TrafficDedupEnabled);
        if (profile.EvaluateMatch && !_matchSettings.MatcherEnabled)
        {
            Warn(profile.StoreCode, "capture-evaluate-disabled", "EvaluateMatch is on but matching is disabled by configuration; plain Capture is used.");
        }

        if (!evaluate)
        {
            return new EffectiveStoreMode(StoreModeKind.Capture, false, false, false, false, null, "profile-capture");
        }

        var dedup = profile.TrafficDedupEnabled && TryResolveTimeZone(profile) is not null;
        return new EffectiveStoreMode(
            StoreModeKind.Capture,
            true,
            profile.StaffExclusionEnabled,
            dedup,
            false,
            dedup ? profile.TimeZoneId!.Trim() : null,
            "profile-capture-evaluate");
    }

    private EffectiveStoreMode ResolveMatch(FaceStoreProfileRow profile)
    {
        if (!_matchSettings.MatcherEnabled)
        {
            // 中文注释：档案要求 Match，但匹配整体关闭：Off（不留生物数据），不是退回 Capture。
            return WarnAndOff(profile, "match-disabled-by-configuration");
        }

        var wantsMatching = profile.StaffExclusionEnabled || profile.TrafficDedupEnabled || profile.UnwelcomeListEnabled;
        if (wantsMatching && !CrossBorderGatePasses(profile))
        {
            Warn(profile.StoreCode, "cross-border-gate", "Match mode requires CrossBorderRegion equal to the configured region plus a review record; the profile is downgraded.");
            return string.IsNullOrWhiteSpace(profile.AllowedSubjects)
                ? EffectiveStoreMode.Off("downgraded-no-allowed-subjects")
                : new EffectiveStoreMode(StoreModeKind.Capture, false, false, false, false, null, "downgraded-to-capture");
        }

        var dedup = profile.TrafficDedupEnabled;
        string? timeZoneId = null;
        if (dedup)
        {
            timeZoneId = TryResolveTimeZone(profile);
            if (timeZoneId is null)
            {
                dedup = false;
            }
        }

        var unwelcome = profile.UnwelcomeListEnabled;
        if (unwelcome && !UnwelcomeGatePasses(profile))
        {
            unwelcome = false;
        }

        return new EffectiveStoreMode(StoreModeKind.Match, false, profile.StaffExclusionEnabled, dedup, unwelcome, timeZoneId, "profile-match");
    }

    private bool CrossBorderGatePasses(FaceStoreProfileRow profile)
        => string.Equals(profile.CrossBorderRegion?.Trim(), _matchSettings.Region, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(profile.CrossBorderReviewRef)
            && !string.IsNullOrWhiteSpace(profile.CrossBorderReviewedBy)
            && profile.CrossBorderReviewedAt is not null;

    private bool UnwelcomeGatePasses(FaceStoreProfileRow profile)
    {
        // 名单是「暗发布」功能（设计第 14.1 节）：主 API 负责强制闸门，接收端再兜一道底，缺任何一项都不启用名单。
        if (string.IsNullOrWhiteSpace(profile.UnwelcomeAssessmentRef)
            || string.IsNullOrWhiteSpace(profile.UnwelcomeLegalSignoffBy)
            || profile.UnwelcomeLegalSignoffAt is null)
        {
            Warn(profile.StoreCode, "unwelcome-gate", "UnwelcomeListEnabled is on but the unwelcome assessment / legal sign-off record is incomplete; the list is not used.");
            return false;
        }

        if (!_hitImageStore.Available)
        {
            Warn(profile.StoreCode, "unwelcome-no-cos", "UnwelcomeListEnabled is on but private COS is not enabled, so hit images cannot be stored; the list is not used.");
            return false;
        }

        return true;
    }

    /// <summary>档案里的时区；找不到（空 / 系统不认识）返回 null 并记 Warning。</summary>
    private string? TryResolveTimeZone(FaceStoreProfileRow profile)
    {
        var id = profile.TimeZoneId?.Trim();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(id);
                return id;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // 落到下面的 Warning。
            }
        }

        Warn(profile.StoreCode, "timezone", "TrafficDedupEnabled needs a resolvable TimeZoneId on the profile (the receiver cannot read the store table); deduplication is not used.");
        return null;
    }

    private EffectiveStoreMode WarnAndOff(FaceStoreProfileRow profile, string reason)
    {
        Warn(profile.StoreCode, reason, "Store profile cannot be honoured; treating the store as Off.");
        return EffectiveStoreMode.Off(reason);
    }

    private void Warn(string storeCode, string key, string message)
    {
        if (_warningGate.TryAcquire(storeCode + "|" + key, out var suppressed))
        {
            _logger.LogWarning(
                "Face match store mode degraded store={StoreCode} reason={Reason}: {Message} suppressedSinceLastLog={SuppressedCount}",
                storeCode,
                key,
                message,
                suppressed);
        }
    }
}

/// <summary>名单命中任务的人脸图存储（私有 COS 前缀 face-ingest/unwelcome-hits/...）。没开 COS 时不可用。</summary>
internal interface IUnwelcomeHitImageStore
{
    /// <summary>是否可用（COS 已启用）。</summary>
    bool Available { get; }

    /// <summary>保存命中图并返回对象键。失败抛异常，由流水线按重试处理。</summary>
    Task<string> SaveAsync(UnwelcomeHitImage image, CancellationToken cancellationToken);
}

/// <summary>待保存的命中图。</summary>
internal sealed record UnwelcomeHitImage(string TaskId, string StoreCode, string CameraId, DateTimeOffset CapturedAtUtc, ReadOnlyMemory<byte> Data, string Sha256);

/// <summary>COS 未启用时的占位：不可用。</summary>
internal sealed class NoUnwelcomeHitImageStore : IUnwelcomeHitImageStore
{
    public bool Available => false;

    public Task<string> SaveAsync(UnwelcomeHitImage image, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Unwelcome hit images require private COS to be enabled.");
}
