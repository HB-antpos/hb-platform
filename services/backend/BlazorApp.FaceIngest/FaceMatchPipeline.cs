using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlazorApp.FaceIngest;

/// <summary>固定的人员库编号（员工库与名单库由主 API 的管理子账号维护，接收端只搜索）。</summary>
internal static class FaceGroups
{
    public const string Staff = "staff";

    public const string Unwelcome = "unwelcome";

    /// <summary>当日访客库编号 <c>v{店}_{yyyyMMdd}</c>（营业日取门店本地日期，设计第 8 节）。</summary>
    public static string Visitor(string storeCode, DateOnly businessDate)
        => string.Create(CultureInfo.InvariantCulture, $"v{storeCode}_{businessDate:yyyyMMdd}");
}

/// <summary>一张待匹配的人脸小图（来自解析器的 Face 类图片）。</summary>
internal sealed record MatchFace(int PartIndex, ReadOnlyMemory<byte> Data, int? Width, int? Height);

/// <summary>一条待匹配的抓拍。</summary>
internal sealed record MatchCapture(
    string CaptureId,
    string CameraId,
    string StoreCode,
    DateTimeOffset ReceivedAtUtc,
    string BodySha256,
    IReadOnlyList<MatchFace> Faces);

/// <summary>一条抓拍的处理结果（仅用于日志与测试断言，不含任何生物信息）。</summary>
internal sealed record MatchOutcome(bool Duplicate, TrafficDelta Delta, int UnwelcomeTasks);

/// <summary>
/// 「匹配后丢弃」流水线（设计第 5、8、14.3 节）。业务口径：
/// - 同一家店的处理<b>串行</b>（每店一把锁）：相邻两条抓拍不会同时判成「新访客」而重复建人；
/// - 每张人脸只发<b>一次</b> SearchFacesReturnsByGroup，groups 按档案开关：员工排除 → staff；去重 → 当日访客库；名单 → unwelcome；
/// - 员工命中优先；去重开启且无访客命中、质量达标 → 在当日访客库建一人并计「唯一访客」，命中则计「重复」；
///   名单命中（≥ 名单阈值，且条目有效）→ 存命中图到私有 COS 并建待复核任务（NotifiedAtUtc 留空，通知由主 API 发）；
/// - 质量不达标 → 低质量计数；腾讯云调用失败 → 有限次退避重试，超限<b>丢弃</b>并计失败，不因调用失败而留存生物数据；
/// - 只写计数（FaceTrafficBucket）；本类不接触本地文件，删除本地报文由处理器负责；
/// - 同摄像机短时间相同请求体哈希不重复搜索也不重复计数；员工命中后同摄像机 StaffSuppressSeconds 内的相邻抓拍直接计员工；
/// - 日志只含摄像机 / 门店 / 结果枚举 / 取整分数 / 计数，不含图片、对象键、密钥、PersonId。
/// 同一个类也承担 Capture 模式的「评估」（只记分数，不写计数桶）。
/// </summary>
internal sealed class FaceMatchPipeline
{
    /// <summary>单条报文最多处理的人脸数，防止异常报文放大腾讯云调用量（海康正常一条一张）。</summary>
    internal const int MaxFacesPerCapture = 5;

    private static readonly TimeSpan WarningLogWindow = TimeSpan.FromMinutes(2);

    private readonly FaceMatchSettings _settings;
    private readonly IFaceMatcher _matcher;
    private readonly IVisitorRegistry _registry;
    private readonly IFaceMatchStore _store;
    private readonly IUnwelcomeHitImageStore _hitImages;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FaceMatchPipeline> _logger;
    private readonly ThrottledLogGate _logGate;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _storeLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastStaffHit = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentBodies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _ensuredGroups = new(StringComparer.Ordinal);
    private long _breakerUntilTicks;

    public FaceMatchPipeline(
        FaceMatchSettings settings,
        IFaceMatcher matcher,
        IVisitorRegistry registry,
        IFaceMatchStore store,
        IUnwelcomeHitImageStore hitImages,
        TimeProvider timeProvider,
        ILogger<FaceMatchPipeline> logger)
    {
        _settings = settings;
        _matcher = matcher;
        _registry = registry;
        _store = store;
        _hitImages = hitImages;
        _timeProvider = timeProvider;
        _logger = logger;
        _logGate = new ThrottledLogGate(timeProvider, WarningLogWindow);
    }

    // ---------------------------------------------------------------- Match 模式

    /// <summary>处理一条 Match 模式的抓拍。成功返回后，调用方即可删除全部本地报文。失败（数据库 / COS 异常）抛异常，由处理器决定重试或丢弃。</summary>
    public async Task<MatchOutcome> ProcessAsync(MatchCapture capture, EffectiveStoreMode mode, CancellationToken cancellationToken)
    {
        var storeLock = _storeLocks.GetOrAdd(capture.StoreCode, static _ => new SemaphoreSlim(1, 1));
        await storeLock.WaitAsync(cancellationToken);
        try
        {
            return await ProcessLockedAsync(capture, mode, cancellationToken);
        }
        finally
        {
            storeLock.Release();
        }
    }

    private async Task<MatchOutcome> ProcessLockedAsync(MatchCapture capture, EffectiveStoreMode mode, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var bodyKey = capture.CameraId + "|" + capture.BodySha256;
        if (_recentBodies.TryGetValue(bodyKey, out var expiresAt) && expiresAt > now)
        {
            // 中文注释：同摄像机短时间内相同请求体 = 断网续传 / 重复推送，不重复搜索、也不重复计数。
            return new MatchOutcome(true, new TrafficDelta(), 0);
        }

        var thresholds = _settings.ThresholdsFor(capture.StoreCode);
        var counters = new Counters();
        VisitorGroup? visitorGroup = null;
        var visitorGroupResolved = false;
        var visitorsAdded = 0;
        var unwelcomeTasks = 0;

        foreach (var face in capture.Faces.Take(MaxFacesPerCapture))
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.Capture++;

            if (mode.StaffExclusion && IsStaffSuppressed(capture))
            {
                counters.StaffMatch++;
                continue;
            }

            if (!PassesQualityGate(face))
            {
                counters.LowQuality++;
                continue;
            }

            // 先确定这张脸要搜哪些库。访客库按营业日懒创建：整条抓拍都不达标时不会白建库。
            if (mode.TrafficDedup && !visitorGroupResolved)
            {
                var resolved = await ResolveVisitorGroupAsync(capture, mode, now, cancellationToken);
                visitorGroupResolved = resolved.Resolved;
                visitorGroup = resolved.Group;
                if (resolved.ApiFailed)
                {
                    counters.ApiFailure++;
                    continue;
                }
            }

            var groups = new List<string>(3);
            if (mode.StaffExclusion)
            {
                groups.Add(FaceGroups.Staff);
            }

            if (mode.Unwelcome)
            {
                groups.Add(FaceGroups.Unwelcome);
            }

            if (visitorGroup is not null)
            {
                groups.Add(visitorGroup.GroupId);
            }

            if (groups.Count == 0)
            {
                // 中文注释：档案没开任何比对，只数「抓拍数」。
                continue;
            }

            var minScore = MinimumThreshold(mode, visitorGroup is not null, thresholds);
            var search = await CallAsync(
                static (state, ct) => state.Matcher.SearchAsync(new FaceSearchRequest(state.Groups, state.Face.Data, state.MinScore), ct),
                (Matcher: _matcher, Groups: (IReadOnlyList<string>)groups, Face: face, MinScore: (double?)minScore),
                capture,
                cancellationToken);
            if (search.Error is { } searchError)
            {
                RecordFailure(counters, searchError);
                continue;
            }

            var result = search.Value!;
            if (result.Status == FaceSearchStatus.NoUsableFace)
            {
                counters.LowQuality++;
                continue;
            }

            // 员工命中优先：员工不计入访客，也不触发名单。
            if (mode.StaffExclusion && result.BestIn(FaceGroups.Staff) is { } staffHit && staffHit.Score >= thresholds.Staff)
            {
                counters.StaffMatch++;
                _lastStaffHit[capture.CameraId] = capture.ReceivedAtUtc;
                continue;
            }

            if (mode.Unwelcome && result.BestIn(FaceGroups.Unwelcome) is { } listHit && listHit.Score >= thresholds.Unwelcome)
            {
                if (await TryCreateUnwelcomeTaskAsync(capture, face, listHit, now, cancellationToken))
                {
                    unwelcomeTasks++;
                }
            }

            if (visitorGroup is not null)
            {
                if (result.BestIn(visitorGroup.GroupId) is { } visitHit && visitHit.Score >= thresholds.Visitor)
                {
                    counters.Repeat++;
                }
                else
                {
                    switch (await CreateVisitorAsync(capture, face, visitorGroup.GroupId, cancellationToken))
                    {
                        case VisitorCreation.Created:
                            counters.UniqueVisitor++;
                            visitorsAdded++;
                            break;
                        case VisitorCreation.LowQuality:
                            counters.LowQuality++;
                            break;
                        default:
                            counters.ApiFailure++;
                            break;
                    }
                }
            }
        }

        var delta = counters.ToDelta();
        if (!delta.IsEmpty)
        {
            // 关键逻辑：一条抓拍的全部计数（含访客库行的 VisitorCount）在同一个事务里写一次。
            await _store.ApplyTrafficAsync(
                new TrafficUpdate(capture.StoreCode, HourStart(capture.ReceivedAtUtc), delta, visitorGroup?.GroupId, visitorsAdded),
                cancellationToken);
        }

        // 只有落库成功才登记「已处理的请求体」，失败重试时不会被当成重复而漏计。
        RememberBody(bodyKey, now);
        return new MatchOutcome(false, delta, unwelcomeTasks);
    }

    // ---------------------------------------------------------------- Capture 模式评估

    /// <summary>
    /// Capture 模式的匹配评估（设计第 13.5 节，用于阈值标定）：对这条抓拍的第一张人脸做一次搜索，
    /// 把员工库 / 当日访客库的最高分和判定写入 FaceMatchEvaluation。不写计数桶、不影响留存。
    /// 为了能得到「访客分」，判定为新访客时同样会在当日访客库建人（内部知情人员，次日整库删除）。
    /// 调用方负责吞掉这里的异常：评估失败不能影响原有入库流程。
    /// </summary>
    public async Task EvaluateAsync(MatchCapture capture, EffectiveStoreMode mode, CancellationToken cancellationToken)
    {
        var face = capture.Faces.FirstOrDefault();
        if (face is null)
        {
            return;
        }

        var storeLock = _storeLocks.GetOrAdd(capture.StoreCode, static _ => new SemaphoreSlim(1, 1));
        await storeLock.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var thresholds = _settings.ThresholdsFor(capture.StoreCode);
            if (!PassesQualityGate(face))
            {
                await _store.InsertEvaluationAsync(new EvaluationRecord(capture.CaptureId, null, null, EvaluationDecision.LowQuality), cancellationToken);
                return;
            }

            VisitorGroup? visitorGroup = null;
            if (mode.TrafficDedup)
            {
                var resolved = await ResolveVisitorGroupAsync(capture, mode, now, cancellationToken);
                if (resolved.ApiFailed)
                {
                    return;
                }

                visitorGroup = resolved.Group;
            }

            var groups = new List<string>(2);
            if (mode.StaffExclusion)
            {
                groups.Add(FaceGroups.Staff);
            }

            if (visitorGroup is not null)
            {
                groups.Add(visitorGroup.GroupId);
            }

            if (groups.Count == 0)
            {
                return;
            }

            // MinScore 为 null：标定要看到所有分数，而不只是过阈值的。
            var search = await CallAsync(
                static (state, ct) => state.Matcher.SearchAsync(new FaceSearchRequest(state.Groups, state.Face.Data, null), ct),
                (Matcher: _matcher, Groups: (IReadOnlyList<string>)groups, Face: face),
                capture,
                cancellationToken);
            if (search.Error is not null)
            {
                return;
            }

            var result = search.Value!;
            if (result.Status == FaceSearchStatus.NoUsableFace)
            {
                await _store.InsertEvaluationAsync(new EvaluationRecord(capture.CaptureId, null, null, EvaluationDecision.LowQuality), cancellationToken);
                return;
            }

            var staffScore = mode.StaffExclusion ? result.BestIn(FaceGroups.Staff)?.Score : null;
            var visitScore = visitorGroup is not null ? result.BestIn(visitorGroup.GroupId)?.Score : null;
            var decision = EvaluationDecision.NoMatch;
            if (staffScore is double staff && staff >= thresholds.Staff)
            {
                decision = EvaluationDecision.Staff;
            }
            else if (visitorGroup is not null)
            {
                if (visitScore is double visit && visit >= thresholds.Visitor)
                {
                    decision = EvaluationDecision.RepeatVisitor;
                }
                else
                {
                    decision = EvaluationDecision.NewVisitor;
                    await CreateVisitorAsync(capture, face, visitorGroup.GroupId, cancellationToken);
                }
            }

            await _store.InsertEvaluationAsync(
                new EvaluationRecord(capture.CaptureId, ToScore(staffScore), ToScore(visitScore), decision),
                cancellationToken);
        }
        finally
        {
            storeLock.Release();
        }
    }

    // ---------------------------------------------------------------- Capture 模式名单检查

    /// <summary>
    /// Capture 模式下的名单检查（只用于内部知情人员的端到端测试，设计第 14 节）：对这条抓拍的每张人脸
    /// 只搜名单库，命中（相似度 ≥ 名单阈值，条目未过期且门店在范围内）就和 Match 一样存命中图并建复核任务。
    /// 不写计数桶、不碰员工库和访客库，也不改变 Capture 原有的留存（事件行与图片照旧）。
    /// 调用方负责吞掉这里的异常：名单检查失败不能影响原有入库流程。
    /// </summary>
    /// <returns>本条抓拍新建的复核任务数。</returns>
    public async Task<int> CheckUnwelcomeAsync(MatchCapture capture, EffectiveStoreMode mode, CancellationToken cancellationToken)
    {
        if (!mode.Unwelcome)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var thresholds = _settings.ThresholdsFor(capture.StoreCode);
        var tasks = 0;
        foreach (var face in capture.Faces.Take(MaxFacesPerCapture))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PassesQualityGate(face))
            {
                continue;
            }

            // 只搜名单库，且用名单阈值作最低分：没过阈值的候选腾讯云不会返回，也就不会有任何无关分数落下来。
            var search = await CallAsync(
                static (state, ct) => state.Matcher.SearchAsync(new FaceSearchRequest(state.Groups, state.Face.Data, state.MinScore), ct),
                (Matcher: _matcher, Groups: (IReadOnlyList<string>)[FaceGroups.Unwelcome], Face: face, MinScore: (double?)thresholds.Unwelcome),
                capture,
                cancellationToken);
            if (search.Error is not null)
            {
                // 账号级 / 请求级错误已在 CallAsync 里按节流告警并熔断；这里不再继续搜这条抓拍的其余人脸。
                return tasks;
            }

            var result = search.Value!;
            if (result.Status == FaceSearchStatus.NoUsableFace)
            {
                continue;
            }

            if (result.BestIn(FaceGroups.Unwelcome) is { } listHit
                && listHit.Score >= thresholds.Unwelcome
                && await TryCreateUnwelcomeTaskAsync(capture, face, listHit, now, cancellationToken))
            {
                tasks++;
            }
        }

        return tasks;
    }

    // ---------------------------------------------------------------- 访客库

    private async Task<VisitorGroupResolution> ResolveVisitorGroupAsync(
        MatchCapture capture,
        EffectiveStoreMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(mode.TimeZoneId ?? string.Empty);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // 策略层已校验过时区；走到这里说明系统时区库在运行中变了，保守地不用去重。
            return new VisitorGroupResolution(true, null, false);
        }

        // 营业日 = 抓拍接收时刻在门店时区的本地日期（不依赖进程所在时区）。
        var localReceived = TimeZoneInfo.ConvertTime(capture.ReceivedAtUtc, timeZone);
        var businessDate = DateOnly.FromDateTime(localReceived.DateTime);
        var deleteAfterUtc = BusinessDayDeleteAfterUtc(businessDate, timeZone);
        if (now.UtcDateTime >= deleteAfterUtc)
        {
            // 中文注释：该营业日的访客库已到删除时间（可能已被主 API 删掉）。不能再往里建人，也不能重新建库留下无人清理的库：
            // 这类迟到的积压抓拍只计抓拍数，不做去重。
            return new VisitorGroupResolution(true, null, false);
        }

        var groupId = FaceGroups.Visitor(capture.StoreCode, businessDate);
        if (!_ensuredGroups.ContainsKey(groupId))
        {
            // 顺序：先登记 FaceVisitGroup 行（让主 API 的清理任务知道有这个库），再去腾讯云建库。
            await _store.EnsureVisitGroupAsync(
                new VisitGroupRecord(groupId, capture.StoreCode, businessDate, now.UtcDateTime, deleteAfterUtc),
                cancellationToken);
            var create = await CallAsync(
                static async (state, ct) =>
                {
                    await state.Registry.CreateGroupAsync(state.GroupId, ct);
                    return true;
                },
                (Registry: _registry, GroupId: groupId),
                capture,
                cancellationToken);
            if (create.Error is not null)
            {
                return new VisitorGroupResolution(false, null, true);
            }

            _ensuredGroups[groupId] = deleteAfterUtc;
            PruneEnsuredGroups(now);
        }

        return new VisitorGroupResolution(true, new VisitorGroup(groupId), false);
    }

    /// <summary>门店本地「营业日次日 02:00」对应的 UTC（设计第 13.3 节 FaceVisitGroup.DeleteAfterUtc）。</summary>
    internal static DateTime BusinessDayDeleteAfterUtc(DateOnly businessDate, TimeZoneInfo timeZone)
    {
        var localDeadline = businessDate.AddDays(1).ToDateTime(new TimeOnly(2, 0), DateTimeKind.Unspecified);
        // 中文注释：夏令时切换日 02:00 可能不存在（春季前移），顺延一小时；秋季 02:00 出现两次时 .NET 取标准时间那一次。
        if (timeZone.IsInvalidTime(localDeadline))
        {
            localDeadline = localDeadline.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(localDeadline, timeZone);
    }

    private async Task<VisitorCreation> CreateVisitorAsync(MatchCapture capture, MatchFace face, string groupId, CancellationToken cancellationToken)
    {
        var personId = BuildVisitorPersonId(capture.CaptureId, face.PartIndex);
        var create = await CallAsync(
            static async (state, ct) =>
            {
                await state.Registry.CreatePersonAsync(state.GroupId, state.PersonId, state.Face.Data, ct);
                return true;
            },
            (Registry: _registry, GroupId: groupId, PersonId: personId, Face: face),
            capture,
            cancellationToken);
        if (create.Error is null)
        {
            return VisitorCreation.Created;
        }

        return create.Error.Kind == FaceMatchErrorKind.Quality ? VisitorCreation.LowQuality : VisitorCreation.Failed;
    }

    /// <summary>
    /// 访客 PersonId：由抓拍编号与人脸序号确定性生成（不可逆哈希，不含业务标识）。
    /// 重试 / 重处理得到同一个 ID，腾讯云返回「已存在」被当成成功，不会因重试多建人。
    /// </summary>
    internal static string BuildVisitorPersonId(string captureId, int partIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"visitor:{captureId}:{partIndex}")));
        return "vp" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static string BuildTaskId(string captureId, int partIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"unwelcome-task:{captureId}:{partIndex}")));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private void PruneEnsuredGroups(DateTimeOffset now)
    {
        // 中文注释：过了删除时间的库不会再被用到，从缓存里清掉，避免长期运行缓存无限增长。
        foreach (var pair in _ensuredGroups)
        {
            if (pair.Value.UtcDateTime < now.UtcDateTime - TimeSpan.FromDays(1))
            {
                _ensuredGroups.TryRemove(pair.Key, out _);
            }
        }
    }

    // ---------------------------------------------------------------- 名单命中

    private async Task<bool> TryCreateUnwelcomeTaskAsync(
        MatchCapture capture,
        MatchFace face,
        FaceSearchHit hit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entry = await _store.FindUnwelcomeEntryAsync(hit.PersonId, cancellationToken);
        if (entry is null)
        {
            if (_logGate.TryAcquire("unwelcome-no-entry|" + capture.StoreCode, out var suppressed))
            {
                _logger.LogWarning(
                    "Face match list hit without a matching entry row; no task created store={StoreCode} suppressedSinceLastLog={SuppressedCount}",
                    capture.StoreCode,
                    suppressed);
            }

            return false;
        }

        // 条目已过期，或限定了其它门店：命中不建任务（主 API 会按到期 DeletePerson，这里只是兜住延迟窗口）。
        if (entry.ExpiresAtUtc <= now.UtcDateTime || !StoreScopeAllows(entry.StoreScope, capture.StoreCode))
        {
            return false;
        }

        var taskId = BuildTaskId(capture.CaptureId, face.PartIndex);
        var imageSha256 = Convert.ToHexStringLower(SHA256.HashData(face.Data.Span));
        var objectKey = await _hitImages.SaveAsync(
            new UnwelcomeHitImage(taskId, capture.StoreCode, capture.CameraId, capture.ReceivedAtUtc, face.Data, imageSha256),
            cancellationToken);
        await _store.InsertMatchTaskAsync(
            new MatchTaskRecord(
                taskId,
                capture.StoreCode,
                capture.CameraId,
                capture.ReceivedAtUtc.UtcDateTime,
                entry.EntryId,
                ToScore(hit.Score)!.Value,
                objectKey,
                now.UtcDateTime.AddDays(_settings.UnwelcomeImageRetentionDays)),
            cancellationToken);
        _logger.LogInformation(
            "Face match list hit task created camera={CameraId} store={StoreCode} scoreRounded={ScoreRounded}",
            capture.CameraId,
            capture.StoreCode,
            (int)Math.Round(hit.Score, MidpointRounding.AwayFromZero));
        return true;
    }

    /// <summary>StoreScope 是门店编码列表（分隔符容错：逗号 / 分号 / 竖线 / 空白 / JSON 括号引号）；空表示不限制。</summary>
    internal static bool StoreScopeAllows(string? storeScope, string storeCode)
    {
        if (string.IsNullOrWhiteSpace(storeScope))
        {
            return true;
        }

        var tokens = storeScope.Split([',', ';', '|', ' ', '\t', '\r', '\n', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(token => string.Equals(token, storeCode, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- 腾讯云调用：退避、熔断、分类

    /// <summary>
    /// 带有限次退避的腾讯云调用。瞬时错误 / 限频指数退避（有上限）；质量类错误不重试；
    /// 账号级错误（认证 / 欠费 / 未开通）告警、不重试，并暂停调用一段时间（熔断）；其它请求级错误告警、不重试。
    /// 成功返回值，失败返回错误；<b>取消异常原样抛出</b>。
    /// </summary>
    private async Task<ApiResult<T>> CallAsync<TState, T>(
        Func<TState, CancellationToken, Task<T>> call,
        TState state,
        MatchCapture capture,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var now = _timeProvider.GetUtcNow();
            if (now.UtcTicks < Interlocked.Read(ref _breakerUntilTicks))
            {
                // 熔断期内不发请求（避免对同一个账号级错误反复打腾讯云），直接按失败处理。
                return new ApiResult<T>(default, new FaceMatchApiException(FaceMatchErrorKind.AccountFatal, "Local.BreakerOpen", "Call"));
            }

            try
            {
                return new ApiResult<T>(await call(state, cancellationToken), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (FaceMatchApiException ex)
            {
                var retryable = ex.Kind is FaceMatchErrorKind.Transient or FaceMatchErrorKind.RateLimited;
                if (!retryable || attempt >= _settings.ApiMaxAttempts)
                {
                    HandleFinalApiError(ex, capture, attempt);
                    return new ApiResult<T>(default, ex);
                }

                await Task.Delay(ComputeRetryDelay(ex.Kind, attempt), _timeProvider, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 中文注释：接口实现抛了意料之外的异常（不是腾讯云错误）：当作请求级错误丢弃这一张，只记类型。
                var wrapped = new FaceMatchApiException(FaceMatchErrorKind.RequestFatal, "Local." + ex.GetType().Name, "Call");
                HandleFinalApiError(wrapped, capture, attempt);
                return new ApiResult<T>(default, wrapped);
            }
        }
    }

    internal TimeSpan ComputeRetryDelay(FaceMatchErrorKind kind, int attempt)
    {
        var baseMs = (double)_settings.ApiRetryBaseDelayMilliseconds * (kind == FaceMatchErrorKind.RateLimited ? 4 : 1);
        var delayMs = baseMs * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20));
        return TimeSpan.FromMilliseconds(Math.Min(delayMs, _settings.ApiRetryMaxDelayMilliseconds));
    }

    private void HandleFinalApiError(FaceMatchApiException error, MatchCapture capture, int attempts)
    {
        if (error.Kind == FaceMatchErrorKind.Quality)
        {
            return;
        }

        var level = LogLevel.Warning;
        if (error.Kind == FaceMatchErrorKind.AccountFatal && error.Code != "Local.BreakerOpen")
        {
            // 账号级错误：升级为 Error 告警，并熔断一段时间。
            level = LogLevel.Error;
            var until = _timeProvider.GetUtcNow().AddSeconds(_settings.FatalCooldownSeconds);
            Interlocked.Exchange(ref _breakerUntilTicks, until.UtcTicks);
        }
        else if (error.Code == "Local.BreakerOpen")
        {
            return;
        }

        if (_logGate.TryAcquire("api|" + error.Code, out var suppressed))
        {
            _logger.Log(
                level,
                "Face match API call failed and was dropped camera={CameraId} store={StoreCode} kind={Kind} code={Code} attempts={Attempts} suppressedSinceLastLog={SuppressedCount}",
                capture.CameraId,
                capture.StoreCode,
                error.Kind,
                error.Code,
                attempts,
                suppressed);
        }
    }

    private static void RecordFailure(Counters counters, FaceMatchApiException error)
    {
        if (error.Kind == FaceMatchErrorKind.Quality)
        {
            counters.LowQuality++;
        }
        else
        {
            counters.ApiFailure++;
        }
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>质量闸门（本地免费）：最小边长与最小体积，用解析器已有的宽高，不引图像库。宽高未知按不达标处理。</summary>
    private bool PassesQualityGate(MatchFace face)
        => face.Width is int width && face.Height is int height
            && Math.Min(width, height) >= _settings.MinFaceEdgePixels
            && face.Data.Length >= _settings.MinFaceBytes;

    private bool IsStaffSuppressed(MatchCapture capture)
    {
        if (_settings.StaffSuppressSeconds <= 0 || !_lastStaffHit.TryGetValue(capture.CameraId, out var last))
        {
            return false;
        }

        var elapsed = capture.ReceivedAtUtc - last;
        return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(_settings.StaffSuppressSeconds);
    }

    private static double MinimumThreshold(EffectiveStoreMode mode, bool hasVisitorGroup, StoreThresholds thresholds)
    {
        var min = double.MaxValue;
        if (mode.StaffExclusion)
        {
            min = Math.Min(min, thresholds.Staff);
        }

        if (hasVisitorGroup)
        {
            min = Math.Min(min, thresholds.Visitor);
        }

        if (mode.Unwelcome)
        {
            min = Math.Min(min, thresholds.Unwelcome);
        }

        return min;
    }

    private void RememberBody(string key, DateTimeOffset now)
    {
        if (_settings.DuplicateBodyWindowSeconds <= 0)
        {
            return;
        }

        _recentBodies[key] = now.AddSeconds(_settings.DuplicateBodyWindowSeconds);
        if (_recentBodies.Count > 5000)
        {
            foreach (var pair in _recentBodies)
            {
                if (pair.Value <= now)
                {
                    _recentBodies.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    internal static DateTime HourStart(DateTimeOffset utc)
    {
        var value = utc.UtcDateTime;
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
    }

    private static decimal? ToScore(double? score)
        => score is double value ? Math.Round((decimal)value, 4, MidpointRounding.AwayFromZero) : null;

    private sealed record VisitorGroup(string GroupId);

    private sealed record VisitorGroupResolution(bool Resolved, VisitorGroup? Group, bool ApiFailed);

    private enum VisitorCreation
    {
        Created,
        LowQuality,
        Failed,
    }

    private sealed record ApiResult<T>(T? Value, FaceMatchApiException? Error);

    private sealed class Counters
    {
        public int Capture;
        public int StaffMatch;
        public int UniqueVisitor;
        public int Repeat;
        public int LowQuality;
        public int ApiFailure;

        public TrafficDelta ToDelta() => new()
        {
            Capture = Capture,
            StaffMatch = StaffMatch,
            UniqueVisitor = UniqueVisitor,
            Repeat = Repeat,
            LowQuality = LowQuality,
            ApiFailure = ApiFailure,
        };
    }
}
