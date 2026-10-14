using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 「命中流」读取服务：以命中任务（FaceMatchTask）为主表，回答「黑名单」视图与红色数字背后的列表。
/// 1. 为什么不用留存抓拍：Match 模式（真实顾客）不留存抓拍，但命中通知照发；用抓拍做主表会出现「红色写 3、点进去是空的」。
///    所以这里按任务查，抓拍与评估只是「有就带上」的补充信息（同摄像机 + 同接收时间对应，见 <see cref="MatchCapturesAsync"/>）；
/// 2. 门店范围与抓拍流 / 复核任务同一套 <see cref="FaceMatchScope"/>（管理员不限，其他人只看自己关联的门店）；
/// 3. 「未读」只看当前登录人自己的那条命中通知（去重键 <c>unwelcome-hit:{TaskId}</c>，见 <see cref="FaceHitReadLookup"/>），
///    与 App 红色数字（<c>unread-count?type=face.unwelcome-hit</c>）同一口径；别人读没读不影响我的未读；
/// 4. 不含姓名、事件细节、对象键与图片；命中图原图仍走独立接口并写审计（本服务不读图）。
/// </summary>
public sealed class FaceHitResultService(
    SqlSugarContext context,
    IFaceMatchSettingsProvider settingsProvider,
    INotificationInboxService inboxService,
    TimeProvider timeProvider) : IFaceHitResultService
{
    public const int MaxPageSize = 50;

    // 抓拍与任务按「同摄像机 + 同接收时间」对应：SQL 里只用 ±10 毫秒的范围取回候选（对 datetime / datetime2 的精度差都不敏感，
    // 且不会给 IN 传一串 datetime 参数），精确对应在内存里按完全相同的时间再筛。
    private static readonly TimeSpan CaptureMatchWindow = TimeSpan.FromMilliseconds(10);

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<FaceMatchPageDto<FaceHitResultDto>> ListAsync(
        FaceHitResultQuery query, FaceMatchActor actor, bool canReview, CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, MaxPageSize);
        var number = Math.Max(1, query.Page);
        var status = ParseHitStatus(query.HitStatus);
        var filter = await BuildFilterAsync(query, actor, cancellationToken);

        var rows = await ApplyStatus(NewQuery(filter), status, filter)
            .OrderBy(t => t.CapturedAtUtc, OrderByType.Desc)
            .OrderBy(t => t.TaskId, OrderByType.Desc)
            .Skip((number - 1) * size)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var pageRows = rows.Take(size).ToList();
        return new FaceMatchPageDto<FaceHitResultDto>
        {
            Items = await BuildDtosAsync(pageRows, actor, canReview, cancellationToken),
            Page = number,
            PageSize = size,
            HasMore = rows.Count > size,
        };
    }

    public async Task<FaceHitSummaryDto> GetSummaryAsync(
        FaceHitResultQuery query, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var filter = await BuildFilterAsync(query, actor, cancellationToken);

        // SqlSugar 的查询对象是可变的，每个计数都从头建一份，不共用同一个实例。
        async Task<int> CountAsync(string status) =>
            await ApplyStatus(NewQuery(filter), status, filter).CountAsync(cancellationToken);

        var pending = await CountAsync(FaceHitStatuses.Pending);
        var dismissed = await CountAsync(FaceHitStatuses.Dismissed);
        var autoConfirmed = await CountAsync(FaceHitStatuses.AutoConfirmed);
        var confirmed = await CountAsync(FaceHitStatuses.Confirmed);
        var unread = await CountAsync(FaceHitStatuses.Unread);
        return new FaceHitSummaryDto
        {
            Total = pending + autoConfirmed + confirmed + dismissed,
            Unread = unread,
            Pending = pending,
            AutoConfirmed = autoConfirmed,
            Confirmed = confirmed,
            Dismissed = dismissed,
        };
    }

    public async Task<FaceHitReadResultDto> MarkReadAsync(
        string taskId, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        // 编号格式不对、任务不存在、不在自己的门店范围：同一个 404，不泄露「某个编号是否存在」。
        if (!FaceMatchObjectKeys.IsValidId(taskId))
        {
            throw FaceMatchErrors.NotFound("任务");
        }

        var storeCode = await _db.Queryable<FaceMatchTask>()
            .Where(t => t.TaskId == taskId)
            .Select(t => t.StoreCode)
            .FirstAsync(cancellationToken);
        var allowedStores = await FaceMatchScope.GetAllowedStoresAsync(_db, actor, cancellationToken);
        if (storeCode == null || (allowedStores != null && !allowedStores.Contains(storeCode)))
        {
            throw FaceMatchErrors.NotFound("任务");
        }

        var dedupeKey = FaceHitNotifier.DedupeKeyPrefix + taskId;
        var notificationId = await _db.Queryable<AppNotification>()
            .Where(n => n.DedupeKey == dedupeKey)
            .Select(n => n.NotificationId)
            .FirstAsync(cancellationToken);
        if (notificationId != null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var userGuid = actor.Id;
            // 只更新本人的未读行：别人的行改不到，重复标记不会刷新已读时间（幂等）。
            await _db.Updateable<AppNotificationRecipient>()
                .SetColumns(r => r.ReadAtUtc == now)
                .Where(r => r.NotificationId == notificationId && r.UserGUID == userGuid && r.ReadAtUtc == null)
                .ExecuteCommandAsync(cancellationToken);
        }

        // 没有通知行（不是收件人 / 通知还没发）也返回 200：App 据此直接刷新红色数字即可。
        return new FaceHitReadResultDto
        {
            UnreadCount = await inboxService.GetUnreadCountAsync(actor.Id, AppNotificationTypes.FaceUnwelcomeHit, cancellationToken),
        };
    }

    // ---------- 查询条件 ----------

    /// <summary>已解析的筛选条件（门店范围、时间范围、当前时间与登录人）；SqlSugar 表达式里只放这里的局部值。</summary>
    private sealed record Filter(
        string? Code,
        bool Restricted,
        List<string> Allowed,
        DateTime? FromUtc,
        DateTime? ToUtc,
        DateTime NowUtc,
        string UserGuid);

    private async Task<Filter> BuildFilterAsync(FaceHitResultQuery query, FaceMatchActor actor, CancellationToken cancellationToken)
    {
        var allowedStores = await FaceMatchScope.GetAllowedStoresAsync(_db, actor, cancellationToken);
        return new Filter(
            string.IsNullOrWhiteSpace(query.StoreCode) ? null : query.StoreCode.Trim(),
            allowedStores != null,
            allowedStores?.ToList() ?? [],
            AsUtcInstant(query.FromUtc),
            AsUtcInstant(query.ToUtc),
            timeProvider.GetUtcNow().UtcDateTime,
            actor.Id);
    }

    /// <summary>
    /// 查询串里的时间统一成 UTC 时刻：带 Z / 偏移的 ISO 时间绑定后可能是本地时间（Kind=Local），先转回 UTC；
    /// 没有时区标记的按 UTC 理解（与接口命名 fromUtc / toUtc 一致）。
    /// </summary>
    private static DateTime? AsUtcInstant(DateTime? value) => value?.Kind switch
    {
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        _ => value,
    };

    private ISugarQueryable<FaceMatchTask> NewQuery(Filter filter)
    {
        var code = filter.Code;
        var restricted = filter.Restricted;
        var allowed = filter.Allowed;
        var fromUtc = filter.FromUtc;
        var toUtc = filter.ToUtc;
        return _db.Queryable<FaceMatchTask>()
            .WhereIF(code != null, t => t.StoreCode == code)
            .WhereIF(restricted, t => allowed.Contains(t.StoreCode))
            .WhereIF(fromUtc.HasValue, t => t.CapturedAtUtc >= fromUtc)
            .WhereIF(toUtc.HasValue, t => t.CapturedAtUtc < toUtc);
    }

    /// <summary>
    /// 叠加状态条件。unread 用「通知 → 本人接收行」的嵌套子查询：未读 = 本人接收行未读，且通知未撤回、未过期
    /// （与收件箱未读数同一口径，见 <see cref="FaceHitReadLookup"/>）。
    /// </summary>
    private static ISugarQueryable<FaceMatchTask> ApplyStatus(ISugarQueryable<FaceMatchTask> query, string status, Filter filter)
    {
        var pending = FaceMatchReviewStatus.Pending;
        var dismissed = FaceMatchReviewStatus.Dismissed;
        var confirmed = FaceMatchReviewStatus.Confirmed;
        var systemId = FaceMatchAuditActions.SystemActorId;
        switch (status)
        {
            case FaceHitStatuses.Pending:
                return query.Where(t => t.ReviewStatus == pending);
            case FaceHitStatuses.Dismissed:
                return query.Where(t => t.ReviewStatus == dismissed);
            case FaceHitStatuses.AutoConfirmed:
                return query.Where(t => t.ReviewStatus == confirmed && t.FirstReviewerId == systemId);
            case FaceHitStatuses.Confirmed:
                // 人工确认 = 已确认且不是系统自动确认（复核人为空的旧数据也算人工）。
                return query.Where(t => t.ReviewStatus == confirmed && (t.FirstReviewerId == null || t.FirstReviewerId != systemId));
            case FaceHitStatuses.Unread:
                var prefix = FaceHitNotifier.DedupeKeyPrefix;
                var userGuid = filter.UserGuid;
                var nowUtc = filter.NowUtc;
                return query.Where(t => SqlFunc.Subqueryable<AppNotification>()
                    .Where(n => n.DedupeKey == SqlFunc.MergeString(prefix, t.TaskId)
                        && n.RevokedAtUtc == null
                        && (n.ExpiresAtUtc == null || n.ExpiresAtUtc > nowUtc)
                        && SqlFunc.Subqueryable<AppNotificationRecipient>()
                            .Where(r => r.NotificationId == n.NotificationId && r.UserGUID == userGuid && r.ReadAtUtc == null)
                            .Any())
                    .Any());
            default:
                return query;
        }
    }

    private static string ParseHitStatus(string? text)
    {
        switch (text?.Trim())
        {
            case null or "" or FaceHitStatuses.All:
                return FaceHitStatuses.All;
            case FaceHitStatuses.Unread:
            case FaceHitStatuses.Pending:
            case FaceHitStatuses.AutoConfirmed:
            case FaceHitStatuses.Confirmed:
            case FaceHitStatuses.Dismissed:
                return text!.Trim();
            default:
                throw FaceMatchErrors.Invalid("hitStatus", "命中状态取值不正确。");
        }
    }

    // ---------- 组装 ----------

    private async Task<List<FaceHitResultDto>> BuildDtosAsync(
        IReadOnlyList<FaceMatchTask> tasks, FaceMatchActor actor, bool canReview, CancellationToken cancellationToken)
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
        var notify = await FaceHitNotifyLookup.LoadAsync(_db, tasks, cancellationToken);
        var reads = await FaceHitReadLookup.LoadAsync(
            _db, actor.Id, tasks.Select(t => t.TaskId).ToList(), timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        var captures = await MatchCapturesAsync(tasks, cancellationToken);
        var highBand = settingsProvider.Options.Unwelcome.HighBandScore;

        return tasks.Select(t =>
        {
            var info = notify.GetValueOrDefault(t.TaskId);
            reads.TryGetValue(t.TaskId, out var read);
            captures.TryGetValue(t.TaskId, out var capture);
            var imageAvailable = !string.IsNullOrEmpty(t.ImageObjectKey);
            var pending = t.ReviewStatus == FaceMatchReviewStatus.Pending;
            return new FaceHitResultDto
            {
                TaskId = t.TaskId,
                CaptureId = capture?.CaptureId,
                StoreCode = t.StoreCode,
                StoreName = storeNames.GetValueOrDefault(t.StoreCode),
                CameraId = t.CameraId,
                CapturedAtUtc = FaceMatchRules.AsUtc(t.CapturedAtUtc),
                ScorePercent = (int)Math.Clamp(Math.Round(t.Score, MidpointRounding.AwayFromZero), 0, 100),
                Band = t.Score >= highBand ? "high" : "normal",
                ReviewStatus = t.ReviewStatus,
                ReviewStatusName = FaceMatchRules.ReviewStatusName(t.ReviewStatus),
                AutoConfirmed = FaceMatchRules.IsAutoConfirmed(t.ReviewStatus, t.FirstReviewerId),
                ImageAvailable = imageAvailable,
                Unread = read?.Unread ?? false,
                NotificationId = read?.NotificationId,
                NotifyState = info?.State ?? FaceHitNotifyStates.Pending,
                NotifiedAtUtc = info?.NotifiedAtUtc,
                NotifyRecipientCount = info?.RecipientCount ?? 0,
                NotifyReadCount = info?.ReadCount ?? 0,
                // 列表本身已按门店范围过滤，所以「在范围内」恒成立；其余三项与复核任务列表同一判断。
                CanReview = canReview && pending && imageAvailable,
                Decision = capture is { Evaluated: true } ? FaceCaptureResultService.DecisionName(capture.Decision) : null,
                StaffScorePercent = capture?.StaffScorePercent,
                VisitScorePercent = capture?.VisitScorePercent,
                FaceCount = capture?.FaceCount,
            };
        }).ToList();
    }

    /// <summary>
    /// 一批任务对应的留存抓拍与评估（TaskId → 抓拍）。接收端建任务时用的是「抓拍的摄像机 + 抓拍的接收时间」，所以反过来按这两列对应；
    /// Match 模式不留存抓拍，没有对应行是正常情况（返回里没有该任务）。
    /// 同一摄像机同一时刻有多条抓拍时优先取不是重复报文的，再按编号取最小的，保证每次结果一致。
    /// </summary>
    private async Task<Dictionary<string, CaptureMatch>> MatchCapturesAsync(
        IReadOnlyList<FaceMatchTask> tasks, CancellationToken cancellationToken)
    {
        // 命中任务可能跨很多天，不能用「最早到最晚」的大范围取抓拍（会把这几个摄像机期间所有抓拍都读回来）：
        // 每个任务各取自己时刻前后 10 毫秒的候选，用 OR 连起来，走（摄像机，接收时间）索引。
        var condition = Expressionable.Create<FaceCaptureEvent>();
        foreach (var pair in tasks.Select(t => (t.CameraId, t.CapturedAtUtc)).Distinct())
        {
            var camera = pair.CameraId;
            var low = pair.CapturedAtUtc - CaptureMatchWindow;
            var high = pair.CapturedAtUtc + CaptureMatchWindow;
            condition.Or(e => e.CameraId == camera && e.ReceivedAtUtc >= low && e.ReceivedAtUtc <= high);
        }

        var events = await _db.Queryable<FaceCaptureEvent>()
            .Where(e => e.ParseStatus == 0)
            .Where(condition.ToExpression())
            .Select(e => new CaptureRow
            {
                CaptureId = e.CaptureId,
                CameraId = e.CameraId,
                ReceivedAtUtc = e.ReceivedAtUtc,
                FaceCount = e.FaceCount,
                DuplicateOfCaptureId = e.DuplicateOfCaptureId,
            })
            .ToListAsync(cancellationToken);
        if (events.Count == 0)
        {
            return [];
        }

        var evaluations = await LoadEvaluationsAsync(events.Select(e => e.CaptureId).ToList());
        var result = new Dictionary<string, CaptureMatch>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var matched = events
                .Where(e => e.CameraId == task.CameraId && e.ReceivedAtUtc == task.CapturedAtUtc)
                .OrderBy(e => string.IsNullOrEmpty(e.DuplicateOfCaptureId) ? 0 : 1)
                .ThenBy(e => e.CaptureId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (matched == null)
            {
                continue;
            }

            var evaluation = evaluations.GetValueOrDefault(matched.CaptureId.Trim());
            result[task.TaskId] = new CaptureMatch(
                matched.CaptureId.Trim(),
                matched.FaceCount,
                evaluation != null,
                evaluation?.Decision ?? 0,
                ToPercent(evaluation?.StaffScore),
                ToPercent(evaluation?.VisitScore));
        }

        return result;
    }

    /// <summary>
    /// 取一批抓拍的评估。CaptureId 是 char(32)，参数显式声明为定长 ANSI，避免 SQL Server 按 nvarchar 隐式转换而放弃主键查找
    /// （评估表一抓拍一行、体量大，不能因此退化成全表扫描；与抓拍流读取图片编号同一做法）。
    /// </summary>
    private async Task<Dictionary<string, EvaluationRow>> LoadEvaluationsAsync(IReadOnlyList<string> captureIds)
    {
        var parameters = new List<SugarParameter>();
        var names = new List<string>();
        for (var i = 0; i < captureIds.Count; i++)
        {
            var name = $"@c{i}";
            names.Add(name);
            parameters.Add(new SugarParameter(name, captureIds[i], System.Data.DbType.AnsiStringFixedLength) { Size = 32 });
        }

        var sql = $"SELECT CaptureId, Decision, StaffScore, VisitScore FROM FaceMatchEvaluation WHERE CaptureId IN ({string.Join(",", names)})";
        var rows = await _db.Ado.SqlQueryAsync<EvaluationRow>(sql, parameters);
        return rows.GroupBy(r => r.CaptureId.Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private static int? ToPercent(decimal? score) =>
        score.HasValue ? (int)Math.Clamp(Math.Round(score.Value, MidpointRounding.AwayFromZero), 0, 100) : null;

    private sealed record CaptureMatch(
        string CaptureId, int FaceCount, bool Evaluated, byte Decision, int? StaffScorePercent, int? VisitScorePercent);

    private sealed class CaptureRow
    {
        public string CaptureId { get; set; } = string.Empty;
        public string CameraId { get; set; } = string.Empty;
        public DateTime ReceivedAtUtc { get; set; }
        public int FaceCount { get; set; }
        public string? DuplicateOfCaptureId { get; set; }
    }

    private sealed class EvaluationRow
    {
        public string CaptureId { get; set; } = string.Empty;
        public byte Decision { get; set; }
        public decimal? StaffScore { get; set; }
        public decimal? VisitScore { get; set; }
    }
}
