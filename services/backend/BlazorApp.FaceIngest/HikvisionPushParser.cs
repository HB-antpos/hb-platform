using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BlazorApp.FaceIngest;

/// <summary>解析结果状态（与 FaceCaptureEvent.ParseStatus 一致，数值不得改动）。</summary>
internal enum CaptureParseStatus : byte
{
    /// <summary>人脸事件完整解析。</summary>
    Parsed = 0,

    /// <summary>解析出了部分内容（事件体或图片缺失 / 报文截断 / 有 faces 却没有人脸图等），细节见 ParseNote。</summary>
    PartiallyParsed = 1,

    /// <summary>什么都没解析出来（空 body、无法识别的格式、解析器异常）；原始报文仍在本地可排障。</summary>
    Unparsed = 2,

    /// <summary>事件体解析成功，但不是人脸事件（VMD、videoloss 等），仍入库便于统计流量。</summary>
    NotFaceEvent = 3,
}

/// <summary>图片类别（与 FaceCaptureImage.Kind 一致，数值不得改动）。</summary>
internal enum CaptureImageKind : byte
{
    Face = 0,
    Background = 1,
    Other = 2,
}

/// <summary>事件体 faces[] 里的一张人脸。</summary>
internal sealed record ParsedFace(
    int Index,
    string? CameraFaceId,
    string? ContentId,
    decimal? RectX,
    decimal? RectY,
    decimal? RectW,
    decimal? RectH,
    bool? RectNormalized,
    decimal? FaceScore,
    string? AttributesJson);

/// <summary>报文里的一张图片；<see cref="Data"/> 是原始 body 的切片，不复制。</summary>
internal sealed record ParsedImage(
    int PartIndex,
    CaptureImageKind Kind,
    string? ContentType,
    ReadOnlyMemory<byte> Data,
    int? FaceIndex,
    int? Width,
    int? Height);

/// <summary>一条推送的解析结果。</summary>
internal sealed record ParsedPush
{
    public required CaptureParseStatus Status { get; init; }

    /// <summary>宽容解析的告警摘要（≤500 字），不含 token、不含图片内容；没有告警为 null。</summary>
    public string? Note { get; init; }

    /// <summary>事件体格式：json | xml；没有事件体为 null。</summary>
    public string? EventFormat { get; init; }

    public string? EventType { get; init; }

    public string? EventState { get; init; }

    /// <summary>报文里的 dateTime（带偏移原样保留）；缺失、解析失败或没有时区偏移时为 null。</summary>
    public DateTimeOffset? CameraEventTime { get; init; }

    public string? MacAddress { get; init; }

    public int? Channel { get; init; }

    public bool? IsRetransmission { get; init; }

    public IReadOnlyList<ParsedFace> Faces { get; init; } = [];

    public IReadOnlyList<ParsedImage> Images { get; init; } = [];

    /// <summary>人脸数：事件体有 faces[] 时取其条数，否则取人脸类图片张数。</summary>
    public int FaceCount { get; init; }
}

/// <summary>
/// 海康 HTTP 监听（Alarm Server）推送报文的宽容解析器（纯函数，线程安全）。
/// 业务口径（调研报告第 2、3 节）：
/// - multipart 边界取自 Content-Type（大小写不敏感、可带引号，常见字面量 <c>boundary</c>）；缺失或对不上时从正文首行 <c>--xxx</c> 推断；
/// - 容忍不规范 part 头：多余分号、无 name、part 头里的 Content-Length（与边界扫描不一致时以边界扫描为准并记 note）、CRLF 与裸 LF 混用；
/// - 事件体 JSON（重复键取第一个出现的值）或 XML（命名空间 hikvision.com / isapi.org，未转义的 &amp; 先修复）；整个 body 是裸 XML / JSON 也接受；
/// - 图片：image/jpeg、image/pjpeg，或没有 Content-Type 但以 FF D8 FF 开头；按 part 名 / filename / Content-ID 归类；
/// - faces[].contentID 与图片 part 名对不上（官方样例就对不上）时按「人脸图出现顺序 ↔ faces[] 顺序」配对并记一次 note。
/// 任何情况下都不向调用方抛异常：异常一律转成 <see cref="CaptureParseStatus.Unparsed"/> + note。
/// </summary>
internal static partial class HikvisionPushParser
{
    private const int MaxNoteLength = 500;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxAttributesJsonLength = 2000;
    private const int MaxCameraFaceIdLength = 64;
    private const int MaxEventTypeLength = 64;
    private const int MaxEventStateLength = 32;
    private const int MaxMacAddressLength = 32;
    private const decimal MaxDecimalValue = 99999.9999m;

    // 中文注释：只保留体貌类小字段，进 AttributesJson；identify / reserve_field（黑名单比对结果、证件号等）与体温等健康数据一律不收（数据最小化）。
    private static readonly string[] AttributeWhitelist =
    [
        "age", "gender", "glass", "mask", "hat", "smile", "beard", "expression", "faceExpression",
        "stayDuration", "captureEndMark", "swingAngle", "tiltAngle", "pupilDistance", "livenessDetectionStatus",
    ];

    private static readonly string[] CoreAttributes = ["age", "gender", "mask", "glass"];

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    /// <summary>解析一条推送。<paramref name="body"/> 是 .bin 的完整内容（≤16MB，直接在内存里处理）。</summary>
    public static ParsedPush Parse(byte[] body, string? contentType)
    {
        var notes = new NoteCollector();
        try
        {
            return ParseCore(body ?? [], contentType, notes);
        }
        catch (Exception ex)
        {
            // 关键逻辑：解析器绝不把异常抛给处理器——解析失败不是「处理失败」，不该重试；原始报文仍在本地。
            notes.Add($"parser error: {ex.GetType().Name}");
            return new ParsedPush { Status = CaptureParseStatus.Unparsed, Note = notes.Build() };
        }
    }

    private static ParsedPush ParseCore(byte[] body, string? contentType, NoteCollector notes)
    {
        if (body.Length == 0)
        {
            notes.Add("empty body");
            return new ParsedPush { Status = CaptureParseStatus.Unparsed, Note = notes.Build() };
        }

        var isMultipartType = contentType?.TrimStart().StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true;
        var boundary = TryGetBoundaryFromContentType(contentType);
        List<RawPart>? parts = null;
        var truncated = false;
        if (boundary is not null)
        {
            parts = SplitMultipart(body, boundary, out truncated);
        }

        if (parts is null)
        {
            // 中文注释：Content-Type 没给边界、给的边界在正文里找不到，或根本没有 Content-Type 时，看正文首行是否形如 --xxx。
            var inferred = InferBoundaryFromBody(body);
            if (inferred is not null)
            {
                parts = SplitMultipart(body, inferred, out truncated);
                if (parts is not null)
                {
                    notes.Add(boundary is not null
                        ? "boundary from Content-Type not found in body; inferred from first line"
                        : string.IsNullOrWhiteSpace(contentType)
                            ? "Content-Type missing; multipart boundary inferred from first line"
                            : "Content-Type has no boundary; inferred from first line");
                }
            }
        }

        if (parts is null)
        {
            if (isMultipartType)
            {
                notes.Add("multipart Content-Type but no boundary delimiter found; treated as single body");
            }

            return ParseSingleBody(body, contentType, notes);
        }

        return ParseMultipart(body, parts, truncated, notes);
    }

    /// <summary>整个 body 不是 multipart：当作只有事件体（裸 XML / JSON），或一张裸图片。</summary>
    private static ParsedPush ParseSingleBody(byte[] body, string? contentType, NoteCollector notes)
    {
        var memory = new ReadOnlyMemory<byte>(body);
        if (IsJpeg(memory.Span))
        {
            notes.Add("body is a bare JPEG without event data");
            var size = JpegDimensions.TryRead(memory.Span);
            return new ParsedPush
            {
                Status = CaptureParseStatus.PartiallyParsed,
                Note = notes.Build(),
                Images = [new ParsedImage(0, CaptureImageKind.Other, "image/jpeg", memory, null, size?.Width, size?.Height)],
            };
        }

        var parsedEvent = TryParseEventBody(memory, contentType, partLabel: "body", notes);
        if (parsedEvent is null)
        {
            notes.Add("body is neither multipart, JSON nor XML");
            return new ParsedPush { Status = CaptureParseStatus.Unparsed, Note = notes.Build() };
        }

        return BuildResult(parsedEvent, [], partial: false, notes);
    }

    private static ParsedPush ParseMultipart(byte[] body, List<RawPart> parts, bool truncated, NoteCollector notes)
    {
        var partial = false;
        if (truncated)
        {
            notes.Add("closing boundary missing (body truncated?)");
            partial = true;
        }

        EventData? parsedEvent = null;
        var eventPartFailed = false;
        var images = new List<MutableImage>();
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = ParsePart(body, parts[partIndex], partIndex, notes);
            var data = part.Data;
            var mediaType = part.MediaType;

            if (IsImagePart(mediaType, data.Span))
            {
                var size = JpegDimensions.TryRead(data.Span);
                images.Add(new MutableImage
                {
                    PartIndex = partIndex,
                    Kind = ClassifyImage(part),
                    ContentType = mediaType ?? "image/jpeg",
                    Data = data,
                    Names = part.Names,
                    Width = size?.Width,
                    Height = size?.Height,
                });
                continue;
            }

            if (IsEventPart(mediaType, data.Span))
            {
                if (parsedEvent is not null)
                {
                    notes.Add($"part #{partIndex} is an extra event body; ignored");
                    continue;
                }

                parsedEvent = TryParseEventBody(data, mediaType, $"part #{partIndex}", notes);
                if (parsedEvent is null)
                {
                    eventPartFailed = true;
                }

                continue;
            }

            if (data.Length == 0)
            {
                notes.Add($"part #{partIndex} is empty");
                continue;
            }

            notes.Add($"part #{partIndex} ignored (Content-Type {SanitizeForNote(mediaType) ?? "missing"})");
        }

        if (eventPartFailed)
        {
            partial = true;
        }

        if (parsedEvent is null)
        {
            if (!eventPartFailed)
            {
                notes.Add("no event body part found");
            }

            if (images.Count == 0)
            {
                return new ParsedPush { Status = CaptureParseStatus.Unparsed, Note = notes.Build() };
            }

            // 中文注释：只有图片没有事件体：图片照样存，状态记「部分解析」。
            var orphanImages = images.Select(static image => image.ToParsed()).ToList();
            return new ParsedPush
            {
                Status = CaptureParseStatus.PartiallyParsed,
                Note = notes.Build(),
                Images = orphanImages,
                FaceCount = orphanImages.Count(static image => image.Kind == CaptureImageKind.Face),
            };
        }

        if (PairFacesWithImages(parsedEvent.Faces, images, notes))
        {
            partial = true;
        }

        return BuildResult(parsedEvent, images.Select(static image => image.ToParsed()).ToList(), partial, notes);
    }

    private static ParsedPush BuildResult(EventData parsedEvent, List<ParsedImage> images, bool partial, NoteCollector notes)
    {
        var faceImages = images.Count(static image => image.Kind == CaptureImageKind.Face);
        var isFaceEvent = parsedEvent.EventType is { } eventType
            ? eventType.Contains("face", StringComparison.OrdinalIgnoreCase)
            : parsedEvent.Faces.Count > 0 || faceImages > 0;
        if (parsedEvent.EventType is null)
        {
            notes.Add("eventType missing");
        }

        var status = !isFaceEvent
            ? CaptureParseStatus.NotFaceEvent
            : partial ? CaptureParseStatus.PartiallyParsed : CaptureParseStatus.Parsed;
        return new ParsedPush
        {
            Status = status,
            Note = notes.Build(),
            EventFormat = parsedEvent.Format,
            EventType = parsedEvent.EventType,
            EventState = parsedEvent.EventState,
            CameraEventTime = parsedEvent.CameraEventTime,
            MacAddress = parsedEvent.MacAddress,
            Channel = parsedEvent.Channel,
            IsRetransmission = parsedEvent.IsRetransmission,
            Faces = parsedEvent.Faces,
            Images = images,
            FaceCount = isFaceEvent ? (parsedEvent.Faces.Count > 0 ? parsedEvent.Faces.Count : faceImages) : 0,
        };
    }

    /// <summary>
    /// 人脸 ↔ 人脸图配对。先按 faces[].contentID 精确匹配图片的 part 名 / filename / Content-ID；
    /// 有任何一张对不上就整体改为「人脸类图片出现顺序 ↔ faces[] 顺序」并记一次 note。返回 true 表示数据有缺口（应记部分解析）。
    /// </summary>
    private static bool PairFacesWithImages(IReadOnlyList<ParsedFace> faces, List<MutableImage> images, NoteCollector notes)
    {
        if (faces.Count == 0)
        {
            return false;
        }

        var assigned = new Dictionary<int, MutableImage>();
        foreach (var face in faces)
        {
            if (string.IsNullOrEmpty(face.ContentId))
            {
                break;
            }

            var match = images.FirstOrDefault(image =>
                !assigned.ContainsValue(image)
                && image.Names.Any(name => string.Equals(name, face.ContentId, StringComparison.OrdinalIgnoreCase)));
            if (match is null)
            {
                break;
            }

            assigned[face.Index] = match;
        }

        if (assigned.Count == faces.Count)
        {
            foreach (var (faceIndex, image) in assigned)
            {
                image.FaceIndex = faceIndex;
                image.Kind = CaptureImageKind.Face;
            }

            return false;
        }

        var faceImages = images.Where(static image => image.Kind == CaptureImageKind.Face).ToList();
        if (faceImages.Count == 0)
        {
            // 中文注释：有 faces[] 却没有人脸图，多半是摄像机「图片上传方式」设成了 URL（云端取不到），需要现场改成 binary。
            notes.Add($"faces[] has {faces.Count} item(s) but no face image part (uploadImagesDataType=URL?)");
            return true;
        }

        notes.Add("faces[].contentID did not match image part names; paired face images with faces[] by order");
        var pairCount = Math.Min(faceImages.Count, faces.Count);
        for (var i = 0; i < pairCount; i++)
        {
            faceImages[i].FaceIndex = faces[i].Index;
        }

        if (faceImages.Count != faces.Count)
        {
            notes.Add($"faces[] count {faces.Count} differs from face image count {faceImages.Count}");
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- multipart 切分

    /// <summary>从 Content-Type 取 boundary 参数：参数名大小写不敏感、值可带引号、容忍多余分号与空白。</summary>
    internal static string? TryGetBoundaryFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        foreach (var parameter in SplitParameters(contentType).Skip(1))
        {
            var equals = parameter.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var name = parameter[..equals].Trim();
            if (!string.Equals(name, "boundary", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Unquote(parameter[(equals + 1)..].Trim());
            return value.Length is > 0 and <= 200 ? value : null;
        }

        return null;
    }

    /// <summary>正文首个非空行形如 <c>--xxx</c> 时取 xxx 作边界（容忍开头的空行）。</summary>
    internal static string? InferBoundaryFromBody(ReadOnlySpan<byte> body)
    {
        var position = 0;
        while (position < body.Length && body[position] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
        {
            position++;
        }

        if (body.Length - position < 3 || body[position] != '-' || body[position + 1] != '-')
        {
            return null;
        }

        var lineEnd = body[position..].IndexOf((byte)'\n');
        var line = lineEnd < 0 ? body[(position + 2)..] : body.Slice(position + 2, lineEnd - 2);
        line = TrimAsciiWhitespace(line);
        if (line.Length is 0 or > 200)
        {
            return null;
        }

        foreach (var b in line)
        {
            if (b is < 0x21 or > 0x7E)
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(line);
    }

    /// <summary>
    /// 按边界扫描切分 part。分隔行必须位于行首（body 开头或紧跟 LF），后面紧跟 CR / LF / 空白 / <c>--</c> 或到达末尾，
    /// 这样图片字节里偶然出现的 <c>--boundary</c> 不会被误切。part 内容不含分隔行前的换行（CRLF 或裸 LF）。
    /// 找不到任何分隔行返回 null；最后一个 part 没有后续分隔行（截断）时 <paramref name="truncated"/>=true。
    /// </summary>
    private static List<RawPart>? SplitMultipart(byte[] body, string boundary, out bool truncated)
    {
        truncated = false;
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var span = body.AsSpan();
        var delimiters = new List<(int Start, int AfterDelimiter, bool IsClose)>();
        var searchFrom = 0;
        while (searchFrom < span.Length)
        {
            var found = span[searchFrom..].IndexOf(delimiter);
            if (found < 0)
            {
                break;
            }

            var start = searchFrom + found;
            var after = start + delimiter.Length;
            searchFrom = start + 1;
            if (start > 0 && span[start - 1] != '\n')
            {
                continue;
            }

            var isClose = after + 1 < span.Length && span[after] == '-' && span[after + 1] == '-';
            if (!isClose && after < span.Length && span[after] is not ((byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t'))
            {
                continue;
            }

            delimiters.Add((start, after, isClose));
            if (isClose)
            {
                break;
            }
        }

        if (delimiters.Count == 0 || delimiters[0].IsClose)
        {
            return null;
        }

        var parts = new List<RawPart>();
        for (var i = 0; i < delimiters.Count; i++)
        {
            var current = delimiters[i];
            if (current.IsClose)
            {
                break;
            }

            // 中文注释：分隔行剩余部分（传输填充空白）连同换行一起跳过。
            var lineEnd = span[current.AfterDelimiter..].IndexOf((byte)'\n');
            var contentStart = lineEnd < 0 ? span.Length : current.AfterDelimiter + lineEnd + 1;
            int contentEnd;
            if (i + 1 < delimiters.Count)
            {
                contentEnd = delimiters[i + 1].Start;
                if (contentEnd > contentStart && span[contentEnd - 1] == '\n')
                {
                    contentEnd--;
                    if (contentEnd > contentStart && span[contentEnd - 1] == '\r')
                    {
                        contentEnd--;
                    }
                }
            }
            else
            {
                truncated = true;
                contentEnd = span.Length;
            }

            parts.Add(new RawPart(contentStart, Math.Max(contentStart, contentEnd)));
        }

        return parts;
    }

    /// <summary>解析一个 part 的头（容忍 CRLF / LF、缺空行、多余分号）并核对 part 头里的 Content-Length。</summary>
    private static PartInfo ParsePart(byte[] body, RawPart rawPart, int partIndex, NoteCollector notes)
    {
        var span = body.AsSpan(rawPart.Start, rawPart.End - rawPart.Start);
        var headers = new List<(string Name, string Value)>();
        var position = 0;
        var headerLimit = Math.Min(span.Length, MaxHeaderBytes);
        var terminated = false;
        while (position < headerLimit)
        {
            var newline = span[position..].IndexOf((byte)'\n');
            var lineLength = newline < 0 ? span.Length - position : newline;
            var line = span.Slice(position, lineLength);
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            if (line.Length == 0)
            {
                position += newline < 0 ? lineLength : lineLength + 1;
                terminated = true;
                break;
            }

            if (!TryParseHeaderLine(line, out var name, out var value))
            {
                // 中文注释：遇到不像头部的行就认为头部结束、内容从这一行开始（缺少空行的不规范 part）。
                if (headers.Count > 0)
                {
                    notes.Add($"part #{partIndex} headers not terminated by blank line");
                }

                break;
            }

            headers.Add((name, value));
            if (newline < 0)
            {
                position = span.Length;
                break;
            }

            position += lineLength + 1;
        }

        if (!terminated && headers.Count > 0 && position >= span.Length)
        {
            notes.Add($"part #{partIndex} has headers but no content");
        }

        var data = new ReadOnlyMemory<byte>(body, rawPart.Start + position, span.Length - position);
        string? mediaType = null;
        var names = new List<string>();
        string? declaredLength = null;
        foreach (var (name, value) in headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && mediaType is null)
            {
                mediaType = value.Split(';', 2)[0].Trim().ToLowerInvariant();
                if (mediaType.Length == 0)
                {
                    mediaType = null;
                }
            }
            else if (name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var parameter in SplitParameters(value).Skip(1))
                {
                    var equals = parameter.IndexOf('=', StringComparison.Ordinal);
                    if (equals <= 0)
                    {
                        continue;
                    }

                    var key = parameter[..equals].Trim();
                    if (key.Equals("name", StringComparison.OrdinalIgnoreCase) || key.Equals("filename", StringComparison.OrdinalIgnoreCase))
                    {
                        AddName(names, Unquote(parameter[(equals + 1)..].Trim()));
                    }
                }
            }
            else if (name.Equals("Content-ID", StringComparison.OrdinalIgnoreCase))
            {
                AddName(names, value.Trim().Trim('<', '>'));
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                declaredLength = value.Trim();
            }
        }

        if (declaredLength is not null)
        {
            if (!long.TryParse(declaredLength, NumberStyles.None, CultureInfo.InvariantCulture, out var declared))
            {
                notes.Add($"part #{partIndex} Content-Length is not a number; boundary scan used");
            }
            else if (declared != data.Length)
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"part #{partIndex} Content-Length {declared} differs from boundary scan {data.Length}; boundary scan used"));
            }
        }

        return new PartInfo(mediaType, names, data);
    }

    private static void AddName(List<string> names, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        names.Add(value);
        // 中文注释：filename 带扩展名（facePicture.jpg），配对 contentID 时也要能用去掉扩展名的形式比对。
        var dot = value.LastIndexOf('.');
        if (dot > 0)
        {
            names.Add(value[..dot]);
        }
    }

    private static bool TryParseHeaderLine(ReadOnlySpan<byte> line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var colon = line.IndexOf((byte)':');
        if (colon <= 0 || colon > 100)
        {
            return false;
        }

        foreach (var b in line[..colon])
        {
            // RFC 7230 token 字符的宽松版本：可见 ASCII，不含空白。
            if (b is < 0x21 or > 0x7E)
            {
                return false;
            }
        }

        name = Encoding.ASCII.GetString(line[..colon]);
        value = Encoding.Latin1.GetString(line[(colon + 1)..]).Trim();
        return true;
    }

    // ---------------------------------------------------------------- 分类

    private static bool IsImagePart(string? mediaType, ReadOnlySpan<byte> data)
    {
        if (mediaType is "image/jpeg" or "image/pjpeg" or "image/jpg")
        {
            return data.Length > 0;
        }

        // 中文注释：没有 Content-Type（或只给了笼统的二进制类型）时按 JPEG 魔数识别。
        return (mediaType is null or "application/octet-stream" or "image/*") && IsJpeg(data);
    }

    private static bool IsEventPart(string? mediaType, ReadOnlySpan<byte> data)
    {
        if (mediaType is not null && (mediaType.Contains("json", StringComparison.Ordinal) || mediaType.Contains("xml", StringComparison.Ordinal)))
        {
            return true;
        }

        if (mediaType is not null && mediaType != "text/plain" && mediaType != "application/octet-stream")
        {
            return false;
        }

        var first = FirstSignificantByte(data);
        return first is (byte)'{' or (byte)'[' or (byte)'<';
    }

    /// <summary>
    /// 按 part 名 / filename / Content-ID 归类：含 background / bkg → 背景图；含 face → 人脸图；其余 Other。
    /// 先判背景：像 faceBackgroundPicture 这类名字是「人脸事件的背景图」，不能当成人脸小图去配对。
    /// </summary>
    private static CaptureImageKind ClassifyImage(PartInfo part)
    {
        var combined = string.Join('|', part.Names).ToLowerInvariant();
        if (combined.Contains("background", StringComparison.Ordinal) || combined.Contains("bkg", StringComparison.Ordinal))
        {
            return CaptureImageKind.Background;
        }

        return combined.Contains("face", StringComparison.Ordinal) ? CaptureImageKind.Face : CaptureImageKind.Other;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> data)
        => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    // ---------------------------------------------------------------- 事件体

    /// <summary>解析事件体（JSON 或 XML）；格式无法识别或解析失败返回 null 并记 note。</summary>
    private static EventData? TryParseEventBody(ReadOnlyMemory<byte> data, string? mediaType, string partLabel, NoteCollector notes)
    {
        var span = StripBomAndTrailingPadding(data.Span);
        var first = FirstSignificantByte(span);
        // 中文注释：以正文首字符为准（固件常把 XML 标成 json 或反过来），首字符看不出来再看 Content-Type。
        var format = first switch
        {
            (byte)'{' or (byte)'[' => "json",
            (byte)'<' => "xml",
            _ when mediaType?.Contains("json", StringComparison.Ordinal) == true => "json",
            _ when mediaType?.Contains("xml", StringComparison.Ordinal) == true => "xml",
            _ => null,
        };
        if (format is null)
        {
            if (span.Length > 0)
            {
                notes.Add($"{partLabel} event body format not recognized");
            }

            return null;
        }

        if (mediaType is not null && !mediaType.Contains(format, StringComparison.Ordinal)
            && (mediaType.Contains("json", StringComparison.Ordinal) || mediaType.Contains("xml", StringComparison.Ordinal)))
        {
            notes.Add($"{partLabel} Content-Type says {SanitizeForNote(mediaType)} but body looks like {format}");
        }

        return format == "json" ? TryParseJson(span, partLabel, notes) : TryParseXml(span, partLabel, notes);
    }

    private static EventData? TryParseJson(ReadOnlySpan<byte> span, string partLabel, NoteCollector notes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(span.ToArray(), JsonOptions);
        }
        catch (JsonException)
        {
            // 中文注释：非法 UTF-8 时按替换字符解码后再试一次；仍失败才放弃。
            try
            {
                document = JsonDocument.Parse(Encoding.UTF8.GetString(span), JsonOptions);
                notes.Add($"{partLabel} JSON is not valid UTF-8; invalid bytes replaced");
            }
            catch (JsonException)
            {
                notes.Add($"{partLabel} JSON could not be parsed");
                return null;
            }
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                notes.Add($"{partLabel} JSON root is not an object");
                return null;
            }

            // 中文注释：个别固件把事件包一层 {"EventNotificationAlert": {...}}，没有顶层 eventType 时往下找一层。
            if (!TryGetFirst(root, "eventType", out _))
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object && TryGetFirst(property.Value, "eventType", out _))
                    {
                        root = property.Value;
                        break;
                    }
                }
            }

            var data = new EventData { Format = "json" };
            data.EventType = Truncate(GetString(root, "eventType"), MaxEventTypeLength);
            data.EventState = Truncate(GetString(root, "eventState"), MaxEventStateLength);
            data.MacAddress = Truncate(GetString(root, "macAddress"), MaxMacAddressLength);
            data.Channel = GetInt(root, "channelID") ?? GetInt(root, "dynChannelID");
            data.IsRetransmission = GetBool(root, "isDataRetransmission");
            data.CameraEventTime = ParseCameraTime(GetString(root, "dateTime"), partLabel, notes);
            data.Faces = ExtractFaces(root, partLabel, notes);
            return data;
        }
    }

    private static List<ParsedFace> ExtractFaces(JsonElement root, string partLabel, NoteCollector notes)
    {
        var faceElements = new List<JsonElement>();
        if (TryGetFirst(root, "faces", out var rootFaces) && rootFaces.ValueKind == JsonValueKind.Array)
        {
            faceElements.AddRange(rootFaces.EnumerateArray());
        }

        // 中文注释：faceCapture[] / alarmResult[] 等数组元素下的 faces[]；同名属性只看第一次出现。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && TryGetFirst(item, "faces", out var faces)
                    && faces.ValueKind == JsonValueKind.Array)
                {
                    faceElements.AddRange(faces.EnumerateArray());
                }
            }
        }

        var result = new List<ParsedFace>();
        foreach (var element in faceElements)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                notes.Add($"{partLabel} faces[] contains a non-object item; skipped");
                continue;
            }

            var index = result.Count;
            string? faceId = null;
            if (TryGetFirst(element, "faceId", out var faceIdElement))
            {
                faceId = faceIdElement.ValueKind switch
                {
                    JsonValueKind.String => faceIdElement.GetString(),
                    JsonValueKind.Number => faceIdElement.GetRawText(),
                    _ => null,
                };
            }

            decimal? x = null, y = null, w = null, h = null;
            bool? normalized = null;
            if (TryGetFirst(element, "faceRect", out var rect) && rect.ValueKind == JsonValueKind.Object)
            {
                var rx = GetDouble(rect, "x");
                var ry = GetDouble(rect, "y");
                var rw = GetDouble(rect, "width");
                var rh = GetDouble(rect, "height");
                if (rx is not null && ry is not null && rw is not null && rh is not null)
                {
                    x = ToDecimal(rx.Value);
                    y = ToDecimal(ry.Value);
                    w = ToDecimal(rw.Value);
                    h = ToDecimal(rh.Value);
                    if (x is null || y is null || w is null || h is null)
                    {
                        notes.Add($"{partLabel} faces[{index}].faceRect out of range; dropped");
                        x = y = w = h = null;
                    }
                    else
                    {
                        // 中文注释：海康两种口径——NVR 样例 0–1 归一化，热成像样例是像素值（如 550.521）。
                        normalized = rx <= 1 && ry <= 1 && rw <= 1 && rh <= 1;
                    }
                }
                else
                {
                    notes.Add($"{partLabel} faces[{index}].faceRect incomplete");
                }
            }

            var score = GetDouble(element, "faceScore");
            result.Add(new ParsedFace(
                index,
                Truncate(faceId, MaxCameraFaceIdLength),
                GetString(element, "contentID"),
                x,
                y,
                w,
                h,
                normalized,
                score is null ? null : ToDecimal(score.Value),
                BuildAttributesJson(element, partLabel, index, notes)));
        }

        return result;
    }

    /// <summary>把白名单里的体貌字段原样拼成小 JSON；超过 2000 字时只留 age/gender/mask/glass，仍超长则丢弃并记 note。</summary>
    private static string? BuildAttributesJson(JsonElement face, string partLabel, int index, NoteCollector notes)
    {
        var json = WriteAttributes(face, AttributeWhitelist);
        if (json is not null && json.Length > MaxAttributesJsonLength)
        {
            json = WriteAttributes(face, CoreAttributes);
            if (json is not null && json.Length > MaxAttributesJsonLength)
            {
                notes.Add($"{partLabel} faces[{index}] attributes too large; dropped");
                return null;
            }

            notes.Add($"{partLabel} faces[{index}] attributes trimmed to core fields");
        }

        return json;
    }

    private static string? WriteAttributes(JsonElement face, IEnumerable<string> names)
    {
        using var buffer = new MemoryStream();
        var written = 0;
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var name in names)
            {
                if (TryGetFirst(face, name, out var value))
                {
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                    written++;
                }
            }

            writer.WriteEndObject();
        }

        return written == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static EventData? TryParseXml(ReadOnlySpan<byte> span, string partLabel, NoteCollector notes)
    {
        var text = Encoding.UTF8.GetString(span);
        // 中文注释：部分固件 XML 里有未转义的 &（如设备名），先把不是实体引用的 & 换成 &amp; 再解析。
        var repaired = UnescapedAmpersandPattern().Replace(text, "&amp;");
        if (!ReferenceEquals(repaired, text) && repaired.Length != text.Length)
        {
            notes.Add($"{partLabel} XML had unescaped '&'; repaired");
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                // 关键逻辑：禁止 DTD 与外部实体，杜绝 XXE / 实体膨胀（报文来自公网）。
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = 4 * 1024 * 1024,
            };
            using var reader = XmlReader.Create(new StringReader(repaired), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            notes.Add($"{partLabel} XML could not be parsed");
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            notes.Add($"{partLabel} XML has no root element");
            return null;
        }

        var ns = root.Name.NamespaceName;
        if (ns.Length > 0
            && !ns.Contains("hikvision.com/ver", StringComparison.OrdinalIgnoreCase)
            && !ns.Contains("isapi.org/ver", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"{partLabel} XML namespace not recognized");
        }

        var data = new EventData { Format = "xml" };
        data.EventType = Truncate(XmlValue(root, "eventType"), MaxEventTypeLength);
        data.EventState = Truncate(XmlValue(root, "eventState"), MaxEventStateLength);
        data.MacAddress = Truncate(XmlValue(root, "macAddress"), MaxMacAddressLength);
        data.Channel = ParseInt(XmlValue(root, "channelID")) ?? ParseInt(XmlValue(root, "dynChannelID"));
        data.IsRetransmission = ParseBool(XmlValue(root, "isDataRetransmission"));
        data.CameraEventTime = ParseCameraTime(XmlValue(root, "dateTime"), partLabel, notes);
        return data;
    }

    /// <summary>按本地名（忽略命名空间）取元素文本：先看直接子元素，再看任意后代。</summary>
    private static string? XmlValue(XElement root, string localName)
    {
        var element = root.Elements().FirstOrDefault(e => e.Name.LocalName == localName)
            ?? root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// 摄像机时间：必须带时区偏移（Z 或 ±hh:mm）才保留——没有偏移就无法知道是哪个时区，置空并记 note，
    /// 原始报文仍在本地；解析失败同样置空并记 note。
    /// </summary>
    private static DateTimeOffset? ParseCameraTime(string? value, string partLabel, NoteCollector notes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!HasUtcOffsetPattern().IsMatch(trimmed))
        {
            notes.Add($"{partLabel} dateTime has no UTC offset; dropped");
            return null;
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        notes.Add($"{partLabel} dateTime could not be parsed");
        return null;
    }

    // ---------------------------------------------------------------- JSON 小工具（重复键取第一个）

    /// <summary>
    /// 取对象里第一个出现的同名属性（官方样例 targetAttrs 里 contentID 出现两次）。
    /// JsonDocument 的 GetProperty / TryGetProperty 遇到重复键取的是最后一个，所以这里自己遍历；
    /// 先精确匹配，找不到再大小写不敏感匹配一次。
    /// </summary>
    internal static bool TryGetFirst(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement? caseInsensitive = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                value = property.Value;
                return true;
            }

            if (caseInsensitive is null && string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitive = property.Value;
            }
        }

        if (caseInsensitive is { } fallback)
        {
            value = fallback;
            return true;
        }

        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetFirst(element, name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!TryGetFirst(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String => ParseInt(value.GetString()),
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!TryGetFirst(element, name, out var value))
        {
            return null;
        }

        double? result = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        return result is { } d && (double.IsNaN(d) || double.IsInfinity(d)) ? null : result;
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!TryGetFirst(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => ParseBool(value.GetString()),
            _ => null,
        };
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool? ParseBool(string? value)
        => bool.TryParse(value?.Trim(), out var flag) ? flag : null;

    /// <summary>转成 decimal(9,4)：四舍五入到 4 位小数，超出 ±99999.9999 返回 null。</summary>
    private static decimal? ToDecimal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > (double)MaxDecimalValue)
        {
            return null;
        }

        return Math.Round((decimal)value, 4, MidpointRounding.AwayFromZero);
    }

    // ---------------------------------------------------------------- 文本小工具

    private static IEnumerable<string> SplitParameters(string value)
    {
        // 中文注释：按分号切参数，引号内的分号不切；空段（多余分号）直接跳过。
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in value)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }

            if (c == ';' && !inQuotes)
            {
                yield return current.ToString().Trim();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        yield return current.ToString().Trim();
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value.Trim();
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        var start = 0;
        var end = span.Length;
        while (start < end && span[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        while (end > start && span[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        return span[start..end];
    }

    private static ReadOnlySpan<byte> StripBomAndTrailingPadding(ReadOnlySpan<byte> span)
    {
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        var end = span.Length;
        while (end > 0 && span[end - 1] is 0 or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        return span[..end];
    }

    private static byte? FirstSignificantByte(ReadOnlySpan<byte> span)
    {
        span = StripBomAndTrailingPadding(span);
        foreach (var b in span)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return b;
            }
        }

        return null;
    }

    private static string? Truncate(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>报文里的字符串进 note 前只保留安全字符并截断，避免把任意内容写进 ParseNote。</summary>
    private static string? SanitizeForNote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var c in value)
        {
            if (builder.Length >= 40)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '+' or '.' or '-' or '_')
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    [GeneratedRegex(@"&(?!(?:[A-Za-z][A-Za-z0-9]{0,31}|#[0-9]{1,8}|#[xX][0-9A-Fa-f]{1,8});)", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedAmpersandPattern();

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HasUtcOffsetPattern();

    // ---------------------------------------------------------------- 内部类型

    private readonly record struct RawPart(int Start, int End);

    private sealed record PartInfo(string? MediaType, IReadOnlyList<string> Names, ReadOnlyMemory<byte> Data);

    private sealed class EventData
    {
        public required string Format { get; init; }

        public string? EventType { get; set; }

        public string? EventState { get; set; }

        public DateTimeOffset? CameraEventTime { get; set; }

        public string? MacAddress { get; set; }

        public int? Channel { get; set; }

        public bool? IsRetransmission { get; set; }

        public List<ParsedFace> Faces { get; set; } = [];
    }

    private sealed class MutableImage
    {
        public int PartIndex { get; init; }

        public CaptureImageKind Kind { get; set; }

        public string? ContentType { get; init; }

        public ReadOnlyMemory<byte> Data { get; init; }

        public IReadOnlyList<string> Names { get; init; } = [];

        public int? FaceIndex { get; set; }

        public int? Width { get; init; }

        public int? Height { get; init; }

        public ParsedImage ToParsed() => new(PartIndex, Kind, ContentType, Data, FaceIndex, Width, Height);
    }

    /// <summary>收集告警：去重、按出现顺序拼接，截断到 500 字。</summary>
    private sealed class NoteCollector
    {
        private readonly List<string> _notes = [];

        public void Add(string note)
        {
            if (!_notes.Contains(note, StringComparer.Ordinal))
            {
                _notes.Add(note);
            }
        }

        public string? Build()
        {
            if (_notes.Count == 0)
            {
                return null;
            }

            var joined = string.Join("; ", _notes);
            return joined.Length <= MaxNoteLength ? joined : joined[..(MaxNoteLength - 1)] + "…";
        }
    }
}

/// <summary>
/// 低成本读取 JPEG 宽高：只扫描段头找 SOF 标记，不解码、不引入图像库。读不出来返回 null。
/// </summary>
internal static class JpegDimensions
{
    public static (int Width, int Height)? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        var position = 2;
        // 中文注释：最多走 512 个段，防止构造的数据让循环过长。
        for (var guard = 0; guard < 512 && position + 3 < data.Length; guard++)
        {
            if (data[position] != 0xFF)
            {
                return null;
            }

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                // 填充字节。
                position++;
                continue;
            }

            position += 2;
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                // EOI / SOS 之后是压缩数据，没找到 SOF 就放弃。
                return null;
            }

            if (position + 2 > data.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (length < 2)
            {
                return null;
            }

            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
            {
                if (position + 7 > data.Length)
                {
                    return null;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 5, 2));
                return width > 0 && height > 0 ? (width, height) : null;
            }

            position += length;
        }

        return null;
    }
}
