
using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 「抓拍与比对结果」读取服务：把留存的抓拍（FaceCaptureEvent）、比对评估（FaceMatchEvaluation）、
/// 名单命中任务（FaceMatchTask）与命中通知的发送情况放在同一行里看。
/// 1. 只读；门店范围与复核任务一致（管理员不限，其他人只看自己关联的门店）；
/// 2. 只列 Capture 模式留存下来的抓拍——Match 模式处理完即删，不会出现在这里（命中任务另见复核页）；
/// 3. 抓拍与命中任务没有外键：任务是用「同一摄像机 + 同一接收时间」生成的，所以按这两列对应（见 <see cref="MatchHits"/>）；
/// 4. 图片内容不在列表里返回：列表只给图片编号（且只在有看图权限时），内容走 <see cref="GetImageAsync"/>，
///    它先校验门店范围，再交给摄像机管理页同一套受保护读取（对象键只来自数据库、类型与字节头校验、大小上限、审计）。
/// </summary>
public sealed class FaceCaptureResultService(
    SqlSugarContext context,
    IFaceCaptureImageService imageService,
    IFaceMatchSettingsProvider settingsProvider) : IFaceCaptureResultService
{
    public const int MaxPageSize = 50;

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9]{1,32}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // FaceCaptureImage.Kind：与接收端 CaptureImageKind 一致（0 人脸小图、1 背景大图）。
    private const byte ImageKindFace = 0;
    private const byte ImageKindBackground = 1;

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<FaceMatchPageDto<FaceCaptureResultDto>> ListAsync(
        FaceCaptureResultQuery query, FaceMatchActor actor, bool canViewImages, CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, MaxPageSize);
        var number = Math.Max(1, query.Page);
        var code = string.IsNullOrWhiteSpace(query.StoreCode) ? null : query.StoreCode.Trim();
        var camera = string.IsNullOrWhiteSpace(query.CameraId) ? null : query.CameraId.Trim();
        var fromUtc = query.FromUtc;
        var toUtc = query.ToUtc;
        var hitOnly = query.HitOnly;
        var decision = ParseDecision(query.Decision);
        var decisionValue = decision.Value.GetValueOrDefault();
        var notEvaluatedOnly = decision.NotEvaluated;
        var allowedStores = await FaceMatchScope.GetAllowedStoresAsync(_db, actor, cancellationToken);
        var allowedList = allowedStores?.ToList() ?? [];
        var restricted = allowedStores != null;

        var rows = await _db.Queryable<FaceCaptureEvent>()
            .LeftJoin<FaceMatchEvaluation>((e, ev) => e.CaptureId == ev.CaptureId)
            .Where((e, ev) => e.ParseStatus == 0)
            .WhereIF(code != null, (e, ev) => e.StoreCode == code)
            .WhereIF(camera != null, (e, ev) => e.CameraId == camera)
            .WhereIF(restricted, (e, ev) => allowedList.Contains(e.StoreCode))
            .WhereIF(fromUtc.HasValue, (e, ev) => e.ReceivedAtUtc >= fromUtc)
            .WhereIF(toUtc.HasValue, (e, ev) => e.ReceivedAtUtc < toUtc)
            .WhereIF(notEvaluatedOnly, (e, ev) => ev.CaptureId == null)
            .WhereIF(decision.Value.HasValue, (e, ev) => ev.Decision == decisionValue)
            .WhereIF(hitOnly, (e, ev) => SqlFunc.Subqueryable<FaceMatchTask>()
                .Where(t => t.CameraId == e.CameraId && t.CapturedAtUtc == e.ReceivedAtUtc)
                .Any())
            .OrderBy((e, ev) => e.ReceivedAtUtc, OrderByType.Desc)
            .Select((e, ev) => new ResultRow
            {
                CaptureId = e.CaptureId,
                StoreCode = e.StoreCode,
                CameraId = e.CameraId,
                ReceivedAtUtc = e.ReceivedAtUtc,
                FaceCount = e.FaceCount,
                DuplicateOfCaptureId = e.DuplicateOfCaptureId,
                EvaluationCaptureId = ev.CaptureId,
                Decision = ev.Decision,
                StaffScore = ev.StaffScore,
                VisitScore = ev.VisitScore,
            })
            .Skip((number - 1) * size)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var pageRows = rows.Take(size).ToList();
        return new FaceMatchPageDto<FaceCaptureResultDto>
        {
            Items = await BuildDtosAsync(pageRows, canViewImages, cancellationToken),
            Page = number,
            PageSize = size,
            HasMore = rows.Count > size,
        };
    }

    public async Task<FaceMatchImageContent> GetImageAsync(
        string captureId, string imageId, FaceMatchActor actor, bool canViewImages, CancellationToken cancellationToken = default)
    {
        // 没有看图权限、编号格式不对、抓拍不存在或不在自己的门店范围，一律同一个 404/403 口径，不泄露「某个编号是否存在」。
        if (!canViewImages)
        {
            throw new FaceMatchException(403, "FACE_IMAGE_FORBIDDEN", "没有权限查看人脸抓拍图片。");
        }

        if (!IdPattern.IsMatch(captureId ?? string.Empty) || !IdPattern.IsMatch(imageId ?? string.Empty))
        {
            throw FaceMatchErrors.NotFound("抓拍图片");
        }

        var captureEvent = await _db.Queryable<FaceCaptureEvent>()
            .Where(e => e.CaptureId == captureId)
            .Select(e => new { e.CameraId, e.StoreCode })
            .FirstAsync(cancellationToken);
        var allowedStores = await FaceMatchScope.GetAllowedStoresAsync(_db, actor, cancellationToken);
        if (captureEvent == null || (allowedStores != null && !allowedStores.Contains(captureEvent.StoreCode)))
        {
            throw FaceMatchErrors.NotFound("抓拍图片");
        }

        try
        {
            var content = await imageService.GetContentAsync(
                captureEvent.CameraId, captureId!, imageId!, new FaceCameraActor(actor.Id, actor.Name), true, cancellationToken);
            return new FaceMatchImageContent(content.Bytes, content.ContentType);
        }
        catch (FaceCameraException ex)
        {
            // 摄像机管理页的图片服务用自己的异常类型，这里转成比对模块的统一业务错误（消息由对方固定构造，不含对象键与地址）。
            throw new FaceMatchException(ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private async Task<List<FaceCaptureResultDto>> BuildDtosAsync(
        IReadOnlyList<ResultRow> rows, bool canViewImages, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var storeCodes = rows.Select(r => r.StoreCode).Distinct().ToList();
        var storeNames = (await _db.Queryable<Store>()
                .Where(s => storeCodes.Contains(s.StoreCode) && !s.IsDeleted)
                .Select(s => new { s.StoreCode, s.StoreName })
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.StoreCode)
            .ToDictionary(g => g.Key, g => g.First().StoreName);

        var images = canViewImages
            ? await LoadImagesAsync(rows.Select(r => r.CaptureId).ToList(), cancellationToken)
            : new Dictionary<string, (string? Face, string? Background)>();
        var hits = await MatchHits(rows, cancellationToken);

        return rows.Select(r =>
        {
            var evaluated = r.EvaluationCaptureId != null;
            images.TryGetValue(r.CaptureId, out var image);
            hits.TryGetValue(r.CaptureId, out var hit);
            return new FaceCaptureResultDto
            {
                CaptureId = r.CaptureId,
                StoreCode = r.StoreCode,
                StoreName = storeNames.GetValueOrDefault(r.StoreCode),
                CameraId = r.CameraId,
                CapturedAtUtc = FaceMatchRules.AsUtc(r.ReceivedAtUtc),
                FaceCount = r.FaceCount,
                IsDuplicate = !string.IsNullOrEmpty(r.DuplicateOfCaptureId),
                Evaluated = evaluated,
                Decision = evaluated ? DecisionName(r.Decision ?? 0) : null,
                StaffScorePercent = ToPercent(r.StaffScore),
                VisitScorePercent = ToPercent(r.VisitScore),
                FaceImageId = image.Face,
                SceneImageId = image.Background,
                Hit = hit,
            };
        }).ToList();
    }

    /// <summary>
    /// 一批抓拍对应的名单命中任务。接收端建任务时用的是「抓拍的摄像机 + 抓拍的接收时间（UTC）」，所以按这两列对应；
    /// 同一抓拍有多张脸命中时只展示相似度最高的一条。
    /// </summary>
    private async Task<Dictionary<string, FaceCaptureResultHitDto>> MatchHits(
        IReadOnlyList<ResultRow> rows, CancellationToken cancellationToken)
    {
        var cameraIds = rows.Select(r => r.CameraId).Distinct().ToList();
        // 时间只用范围条件取回（避免给 IN 传一串 datetime 参数），精确对应在内存里按「摄像机 + 完全相同的时间」再筛。
        var earliest = rows.Min(r => r.ReceivedAtUtc);
        var latest = rows.Max(r => r.ReceivedAtUtc);
        var tasks = await _db.Queryable<FaceMatchTask>()
            .Where(t => cameraIds.Contains(t.CameraId) && t.CapturedAtUtc >= earliest && t.CapturedAtUtc <= latest)
            .ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return [];
        }

        var notify = await FaceHitNotifyLookup.LoadAsync(_db, tasks, cancellationToken);
        var highBand = settingsProvider.Options.Unwelcome.HighBandScore;
        var result = new Dictionary<string, FaceCaptureResultHitDto>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var best = tasks
                .Where(t => t.CameraId == row.CameraId && t.CapturedAtUtc == row.ReceivedAtUtc)
                .OrderByDescending(t => t.Score)
                .FirstOrDefault();
            if (best == null)
            {
                continue;
            }

            var info = notify.GetValueOrDefault(best.TaskId);
            result[row.CaptureId] = new FaceCaptureResultHitDto
            {
                TaskId = best.TaskId,
                ScorePercent = (int)Math.Clamp(Math.Round(best.Score, MidpointRounding.AwayFromZero), 0, 100),
                Band = best.Score >= highBand ? "high" : "normal",
                ReviewStatus = best.ReviewStatus,
                ReviewStatusName = FaceMatchRules.ReviewStatusName(best.ReviewStatus),
                ImageAvailable = !string.IsNullOrEmpty(best.ImageObjectKey),
                NotifyState = info?.State ?? FaceHitNotifyStates.Pending,
                NotifiedAtUtc = info?.NotifiedAtUtc,
                NotifyRecipientCount = info?.RecipientCount ?? 0,
                NotifyReadCount = info?.ReadCount ?? 0,
            };
        }

        return result;
    }

    /// <summary>
    /// 每个抓拍取一张人脸小图和一张背景大图的编号。CaptureId 是 char(32)，参数显式声明为定长 ANSI，
    /// 避免 SQL Server 按 nvarchar 隐式转换而放弃索引（与摄像机管理页的图片读取同一做法）。
    /// </summary>
    private async Task<Dictionary<string, (string? Face, string? Background)>> LoadImagesAsync(
        IReadOnlyList<string> captureIds, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var parameters = new List<SugarParameter>();
        var names = new List<string>();
        for (var i = 0; i < captureIds.Count; i++)
        {
            var name = $"@c{i}";
            names.Add(name);
            parameters.Add(new SugarParameter(name, captureIds[i], System.Data.DbType.AnsiStringFixedLength) { Size = 32 });
        }

        var sql = $"SELECT ImageId, CaptureId, Kind, PartIndex FROM FaceCaptureImage WHERE CaptureId IN ({string.Join(",", names)})";
        var imageRows = await _db.Ado.SqlQueryAsync<ImageRow>(sql, parameters);
        var result = new Dictionary<string, (string? Face, string? Background)>(StringComparer.Ordinal);
        foreach (var group in imageRows.GroupBy(i => i.CaptureId.Trim(), StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(i => i.PartIndex).ToList();
            result[group.Key] = (
                ordered.FirstOrDefault(i => i.Kind == ImageKindFace)?.ImageId.Trim(),
                ordered.FirstOrDefault(i => i.Kind == ImageKindBackground)?.ImageId.Trim());
        }

        return result;
    }

    // ---------- 判定值映射（与接收端 FaceMatchDecision 一致：0 NoMatch、1 Staff、2 RepeatVisitor、3 NewVisitor、4 LowQuality） ----------

    internal static string DecisionName(byte value) => value switch
    {
        1 => "staff",
        2 => "repeatVisitor",
        3 => "newVisitor",
        4 => "lowQuality",
        _ => "noMatch",
    };

    private static (byte? Value, bool NotEvaluated) ParseDecision(string? text)
    {
        switch (text?.Trim())
        {
            case null or "":
                return (null, false);
            case "noMatch":
                return (0, false);
            case "staff":
                return (1, false);
            case "repeatVisitor":
                return (2, false);
            case "newVisitor":
                return (3, false);
            case "lowQuality":
                return (4, false);
            case "notEvaluated":
                return (null, true);
            default:
                throw FaceMatchErrors.Invalid("decision", "判定结果取值不正确。");
        }
    }

    private static int? ToPercent(decimal? score) =>
        score.HasValue ? (int)Math.Clamp(Math.Round(score.Value, MidpointRounding.AwayFromZero), 0, 100) : null;

    private sealed class ResultRow
    {
        public string CaptureId { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string CameraId { get; set; } = string.Empty;
        public DateTime ReceivedAtUtc { get; set; }
        public int FaceCount { get; set; }
        public string? DuplicateOfCaptureId { get; set; }
        public string? EvaluationCaptureId { get; set; }
        public byte? Decision { get; set; }
        public decimal? StaffScore { get; set; }
        public decimal? VisitScore { get; set; }
    }

    private sealed class ImageRow
    {
        public string ImageId { get; set; } = string.Empty;
        public string CaptureId { get; set; } = string.Empty;
        public byte Kind { get; set; }
        public int PartIndex { get; set; }
    }
}
