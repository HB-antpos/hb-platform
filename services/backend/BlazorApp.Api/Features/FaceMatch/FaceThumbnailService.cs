using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Shared.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 抓拍 / 命中图的缩略图批量读取（手机端列表的头像用）。生物识别信息，保护措施：
/// 1. 权限由控制器的策略保证（FaceCapture.Images.View）；门店范围与抓拍流同一套 <see cref="FaceMatchScope"/>；
/// 2. 对象键只来自数据库：客户端只传抓拍 / 任务编号；编号格式不对、不存在、不在自己门店范围的项一律 <c>denied</c>，
///    对外同一口径，不泄露「某个编号是否存在」；
/// 3. 读取时做与整图读取相同的校验：存储必须是 COS、声明大小不超过上限、类型只认 jpeg / png 且字节头必须相符、
///    命中图的键必须通过 <see cref="FaceMatchObjectKeys.IsAllowedHitImageKey"/>（不能指向登记照 / 同意书 / 参考照片）；
/// 4. 服务端解码后缩成最长边 128 像素的 JPEG（质量约 70），<b>不保留任何元数据</b>（EXIF / ICC / IPTC / XMP）；
///    超大像素的图片拒绝解码（防解压炸弹），单项失败（读取失败 / 内容不合法）只让该项 <c>gone</c>，不拖垮整批；
/// 5. 整批最多 30 项、并发读取存储最多 4 路，避免一次请求打满存储；
/// 6. <b>审计：一次请求恰好一条</b>（<see cref="FaceMatchAuditActions.ThumbnailsView"/>），不逐张记；
///    不含图片、对象键与任何抓拍 / 任务编号；写入失败整个请求失败、不返回任何图片字节；
/// 7. 日志里没有编号、对象键与图片：读取失败只按原因（错误码 / 异常类型）汇总计数。
/// 为什么不直接调 <see cref="IFaceCaptureImageService.GetContentAsync"/>：它逐张写结构化日志（含抓拍与图片编号），
/// 批量调用会违反「一次请求一条、不含编号」；这里复用它的校验函数与同一个对象存储读取器，行为一致但不逐张留痕。
/// </summary>
public sealed class FaceThumbnailService(
    SqlSugarContext context,
    IFaceImageStore captureImageStore,
    IFaceMatchObjectStore hitImageStore,
    IFaceMatchAudit audit,
    ILogger<FaceThumbnailService> logger) : IFaceThumbnailService
{
    public const int MaxItems = 30;

    /// <summary>缩略图最长边（像素）。</summary>
    public const int MaxEdge = 128;

    public const int JpegQuality = 70;

    /// <summary>同时读取存储的最大路数。</summary>
    public const int MaxConcurrentReads = 4;

    // 解码前先看头部：边长或总像素超过这些上限的图片不解码（5MB 以内的 JPEG 也可能声明极大尺寸）。
    // 1600 万像素约占 48MB（Rgb24），并发 4 路时最坏也就两三百 MB；4K 画面（约 830 万）与手机 1200 万像素照片都在上限内。
    private const int MaxSourceEdge = 8192;
    private const long MaxSourcePixels = 16_000_000;

    // FaceCaptureImage.Kind：0 人脸小图（与接收端 CaptureImageKind 一致）。
    private const byte ImageKindFace = 0;

    private const int MaxEchoIdLength = 64;

    // 抓拍编号：与抓拍流读图同一个宽松格式（1–32 位字母数字），只为在查库前挡掉明显的垃圾输入。
    private static readonly Regex CaptureIdPattern = new(@"^[A-Za-z0-9]{1,32}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<FaceThumbnailResponse> GetThumbnailsAsync(
        FaceThumbnailRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var items = request?.Items;
        if (items == null || items.Count < 1 || items.Count > MaxItems)
        {
            throw FaceMatchErrors.Invalid("items", $"一次请求需要 1 到 {MaxItems} 项。");
        }

        // 1) 整理请求项：种类不认识是协议错误（400）；编号格式不对只让该项 denied。
        var slots = new List<Slot>(items.Count);
        foreach (var item in items)
        {
            var kind = item?.Kind?.Trim();
            if (kind is not (FaceThumbnailKinds.Capture or FaceThumbnailKinds.Hit))
            {
                throw FaceMatchErrors.Invalid("items", "缩略图种类只能是 capture 或 hit。");
            }

            var id = item?.Id ?? string.Empty;
            var valid = kind == FaceThumbnailKinds.Capture ? CaptureIdPattern.IsMatch(id) : FaceMatchObjectKeys.IsValidId(id);
            slots.Add(new Slot(kind, id, valid));
        }

        // 2) 一次性查库解析（门店范围、图片行）；同一个 (种类, 编号) 只解析、只读取一次。
        var allowedStores = await FaceMatchScope.GetAllowedStoresAsync(_db, actor, cancellationToken);
        var distinct = slots.Where(s => s.Valid).Select(s => (s.Kind, s.Id)).Distinct().ToList();
        var captures = await ResolveCapturesAsync(
            distinct.Where(p => p.Kind == FaceThumbnailKinds.Capture).Select(p => p.Id).ToList(), allowedStores);
        var hits = await ResolveHitsAsync(
            distinct.Where(p => p.Kind == FaceThumbnailKinds.Hit).Select(p => p.Id).ToList(), allowedStores);

        var resolved = new Dictionary<(string Kind, string Id), Resolved>();
        foreach (var (kind, id) in distinct)
        {
            resolved[(kind, id)] = kind == FaceThumbnailKinds.Capture
                ? captures.GetValueOrDefault(id) ?? Resolved.Denied
                : hits.GetValueOrDefault(id) ?? Resolved.Denied;
        }

        // 3) 并发读取并缩图（最多 4 路）；失败只按原因计数。
        var failures = new FailureCounter();
        using var gate = new SemaphoreSlim(MaxConcurrentReads);
        var outcomes = new Dictionary<(string Kind, string Id), string?>();
        var statuses = new Dictionary<(string Kind, string Id), string>();
        var work = resolved.Select(async pair =>
        {
            var (key, target) = (pair.Key, pair.Value);
            if (target.Status != FaceThumbnailStatuses.Ok)
            {
                return (key, target.Status, (string?)null);
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                var base64 = await ReadThumbnailAsync(key.Kind, target, failures, cancellationToken);
                return base64 == null ? (key, FaceThumbnailStatuses.Gone, (string?)null) : (key, FaceThumbnailStatuses.Ok, base64);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        foreach (var (key, status, base64) in await Task.WhenAll(work))
        {
            statuses[key] = status;
            outcomes[key] = base64;
        }

        if (failures.Total > 0)
        {
            // 只有汇总计数与原因（错误码 / 异常类型），没有编号、对象键、地址或图片。
            logger.LogWarning("人脸缩略图批量读取有 {Count} 项失败: {Reasons}", failures.Total, failures.Describe());
        }

        // 4) 一次请求恰好一条审计：先于返回字节写入，写失败则整体失败、不返回任何图片。
        var okCount = statuses.Values.Count(s => s == FaceThumbnailStatuses.Ok);
        var touchedStores = resolved.Values
            .Where(r => r.StoreCode != null)
            .Select(r => r.StoreCode!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        await audit.WriteAsync(
            actor,
            FaceMatchAuditActions.ThumbnailsView,
            touchedStores.Count == 1 ? touchedStores[0] : null,
            targetRef: null,
            $"n={okCount}",
            cancellationToken);

        // 5) 与请求同序返回。
        var response = slots.Select(slot =>
        {
            var echoId = slot.Id.Length > MaxEchoIdLength ? slot.Id[..MaxEchoIdLength] : slot.Id;
            if (!slot.Valid || !statuses.TryGetValue((slot.Kind, slot.Id), out var status))
            {
                return new FaceThumbnailItemDto { Kind = slot.Kind, Id = echoId, Status = FaceThumbnailStatuses.Denied };
            }

            return new FaceThumbnailItemDto
            {
                Kind = slot.Kind,
                Id = echoId,
                Status = status,
                ImageBase64 = status == FaceThumbnailStatuses.Ok ? outcomes[(slot.Kind, slot.Id)] : null,
            };
        }).ToList();
        return new FaceThumbnailResponse { Items = response };
    }

    // ---------- 解析：编号 → 门店范围 + 图片位置 ----------

    /// <summary>
    /// 抓拍 → 人脸小图。CaptureId 是 char(32)，参数显式声明为定长 ANSI，避免 SQL Server 按 nvarchar 隐式转换而放弃主键 / 索引查找。
    /// 每个抓拍取 PartIndex 最小的一张人脸小图。
    /// </summary>
    private async Task<Dictionary<string, Resolved>> ResolveCapturesAsync(IReadOnlyList<string> ids, HashSet<string>? allowedStores)
    {
        var result = new Dictionary<string, Resolved>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return result;
        }

        var (names, parameters) = FixedLengthParameters("@c", ids);
        var sql = $"""
SELECT e.CaptureId AS CaptureId, e.StoreCode AS StoreCode, i.Storage AS Storage, i.ObjectKey AS ObjectKey,
       i.ContentType AS ContentType, i.ByteSize AS ByteSize, i.PartIndex AS PartIndex
FROM FaceCaptureEvent e
LEFT JOIN FaceCaptureImage i ON i.CaptureId = e.CaptureId AND i.Kind = {ImageKindFace}
WHERE e.CaptureId IN ({string.Join(",", names)})
""";
        var rows = await _db.Ado.SqlQueryAsync<CaptureImageRow>(sql, parameters);
        foreach (var group in rows.GroupBy(r => r.CaptureId.Trim(), StringComparer.Ordinal))
        {
            var storeCode = group.First().StoreCode;
            if (allowedStores != null && !allowedStores.Contains(storeCode))
            {
                continue;   // 不在门店范围：不放进结果，对外就是 denied（与不存在同一口径）
            }

            var image = group.Where(r => r.ObjectKey != null).OrderBy(r => r.PartIndex).FirstOrDefault();
            if (image == null || !string.Equals(image.Storage?.Trim(), "cos", StringComparison.OrdinalIgnoreCase))
            {
                // 没有人脸小图，或图片还在接收端本地磁盘（后台读不到）：没有可用的留存图。
                result[group.Key] = new Resolved(FaceThumbnailStatuses.None, storeCode, null, null, 0);
                continue;
            }

            result[group.Key] = new Resolved(FaceThumbnailStatuses.Ok, storeCode, image.ObjectKey, image.ContentType, image.ByteSize);
        }

        return result;
    }

    /// <summary>任务 → 命中图键。TaskId 是 char(32)，同样用定长 ANSI 参数。</summary>
    private async Task<Dictionary<string, Resolved>> ResolveHitsAsync(IReadOnlyList<string> ids, HashSet<string>? allowedStores)
    {
        var result = new Dictionary<string, Resolved>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return result;
        }

        var (names, parameters) = FixedLengthParameters("@t", ids);
        var sql = $"SELECT TaskId, StoreCode, ImageObjectKey FROM FaceMatchTask WHERE TaskId IN ({string.Join(",", names)})";
        var rows = await _db.Ado.SqlQueryAsync<HitImageRow>(sql, parameters);
        foreach (var row in rows)
        {
            if (allowedStores != null && !allowedStores.Contains(row.StoreCode))
            {
                continue;
            }

            if (string.IsNullOrEmpty(row.ImageObjectKey))
            {
                // 任务没有命中图键（接收端写任务时必填，清空只会发生在到期清理之后）：没有可用的留存图。
                result[row.TaskId.Trim()] = new Resolved(FaceThumbnailStatuses.None, row.StoreCode, null, null, 0);
            }
            else if (!FaceMatchObjectKeys.IsAllowedHitImageKey(row.ImageObjectKey))
            {
                // 键形态不合法（库里被改成别的私有目录等）：不去读，按读取不到处理。
                result[row.TaskId.Trim()] = new Resolved(FaceThumbnailStatuses.Gone, row.StoreCode, null, null, 0);
            }
            else
            {
                result[row.TaskId.Trim()] = new Resolved(FaceThumbnailStatuses.Ok, row.StoreCode, row.ImageObjectKey, null, 0);
            }
        }

        return result;
    }

    private static (List<string> Names, List<SugarParameter> Parameters) FixedLengthParameters(string prefix, IReadOnlyList<string> values)
    {
        var names = new List<string>();
        var parameters = new List<SugarParameter>();
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"{prefix}{i}";
            names.Add(name);
            parameters.Add(new SugarParameter(name, values[i], System.Data.DbType.AnsiStringFixedLength) { Size = 32 });
        }

        return (names, parameters);
    }

    // ---------- 读取与缩图 ----------

    /// <summary>读取一项并缩图；任何失败返回 null（该项 gone），失败原因只计数。取消请求原样抛出。</summary>
    private async Task<string?> ReadThumbnailAsync(string kind, Resolved target, FailureCounter failures, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            if (kind == FaceThumbnailKinds.Capture)
            {
                // 与 FaceCaptureImageService.GetContentAsync 同一套校验：声明大小、类型白名单、字节头。
                if (target.ByteSize > FaceCaptureImageService.MaxImageBytes)
                {
                    failures.Add("too-large");
                    return null;
                }

                var contentType = FaceCaptureImageService.NormalizeContentType(target.ContentType);
                if (contentType == null)
                {
                    failures.Add("type-unsupported");
                    return null;
                }

                bytes = await captureImageStore.ReadAsync(target.ObjectKey!, FaceCaptureImageService.MaxImageBytes, cancellationToken);
                if (!FaceCaptureImageService.MatchesMagicBytes(bytes, contentType))
                {
                    failures.Add("type-unsupported");
                    return null;
                }
            }
            else
            {
                bytes = await hitImageStore.ReadAsync(target.ObjectKey!, FaceMatchRules.MaxImageBytes, cancellationToken);
                if (FaceMatchRules.DetectImage(bytes) == null)
                {
                    failures.Add("type-unsupported");
                    return null;
                }
            }

            var thumbnail = MakeThumbnail(bytes);
            if (thumbnail == null)
            {
                failures.Add("undecodable");
                return null;
            }

            return Convert.ToBase64String(thumbnail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FaceCameraException ex)
        {
            failures.Add(ex.Code);   // 固定错误码（如 FACE_IMAGE_OBJECT_MISSING），不含键 / 地址
            return null;
        }
        catch (FaceMatchStoreException ex)
        {
            failures.Add($"store-{ex.Kind}");
            return null;
        }
        catch (Exception ex)
        {
            failures.Add(ex.GetType().Name);   // 只记异常类型，不记消息（可能带路径 / 主机名）
            return null;
        }
    }

    /// <summary>
    /// 解码 → 按 EXIF 方向转正 → 最长边缩到 128（不放大）→ 清掉全部元数据 → 质量约 70 的 JPEG。
    /// 解码前先看头部尺寸（边长不超过 8192、总像素不超过 1600 万），超限或不是图片返回 null。
    /// </summary>
    internal static byte[]? MakeThumbnail(byte[] source)
    {
        try
        {
            var info = Image.Identify(source);
            if (info == null
                || info.Width <= 0 || info.Height <= 0
                || info.Width > MaxSourceEdge || info.Height > MaxSourceEdge
                || (long)info.Width * info.Height > MaxSourcePixels)
            {
                return null;
            }

            using var image = Image.Load<Rgb24>(source);
            image.Mutate(x => x.AutoOrient());
            var longest = Math.Max(image.Width, image.Height);
            if (longest > MaxEdge)
            {
                var scale = (double)MaxEdge / longest;
                var width = Math.Max(1, (int)Math.Round(image.Width * scale));
                var height = Math.Max(1, (int)Math.Round(image.Height * scale));
                image.Mutate(x => x.Resize(width, height));
            }

            // 缩略图不带任何元数据：EXIF（含 GPS / 设备信息）、ICC、IPTC、XMP 全部去掉。
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;
            using var output = new MemoryStream();
            image.SaveAsJpeg(output, new JpegEncoder { Quality = JpegQuality });
            return output.ToArray();
        }
        catch (ImageFormatException)
        {
            // 不认识的格式 / 内容损坏或被截断（UnknownImageFormatException、InvalidImageContentException 的共同基类）。
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    // ---------- 内部类型 ----------

    private sealed record Slot(string Kind, string Id, bool Valid);

    /// <summary>
    /// 一个编号的解析结果：Status = ok 才会去读存储；<see cref="StoreCode"/> 非空表示编号存在且在自己的门店范围内（用于审计的门店）。
    /// </summary>
    private sealed record Resolved(string Status, string? StoreCode, string? ObjectKey, string? ContentType, int ByteSize)
    {
        public static readonly Resolved Denied = new(FaceThumbnailStatuses.Denied, null, null, null, 0);
    }

    /// <summary>读取失败按原因计数（线程安全，多路并发读取共用）。</summary>
    private sealed class FailureCounter
    {
        private readonly Dictionary<string, int> _reasons = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public int Total { get; private set; }

        public void Add(string reason)
        {
            lock (_gate)
            {
                Total++;
                _reasons[reason] = _reasons.GetValueOrDefault(reason) + 1;
            }
        }

        public string Describe()
        {
            lock (_gate)
            {
                return string.Join(", ", _reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
            }
        }
    }

    private sealed class CaptureImageRow
    {
        public string CaptureId { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string? Storage { get; set; }
        public string? ObjectKey { get; set; }
        public string? ContentType { get; set; }
        public int ByteSize { get; set; }
        public int PartIndex { get; set; }
    }

    private sealed class HitImageRow
    {
        public string TaskId { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string ImageObjectKey { get; set; } = string.Empty;
    }
}
