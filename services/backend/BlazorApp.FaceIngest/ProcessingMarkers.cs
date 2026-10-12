using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 处理器旁路标记文件的内容（camelCase JSON，与报文同目录、同名 + 后缀）。
/// 任何字段都不含 token；lastError 只有异常类型与消息摘要。
/// </summary>
internal static class ProcessingMarkers
{
    public const int SchemaVersion = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 不连库时写的本地解析摘要 <c>{name}.parsed.json</c>：字段与 FaceCaptureEvent / FaceCaptureImage 的列一一对应，
    /// 便于离线分析解析效果；连库后不再写（以库为准）。
    /// </summary>
    public static byte[] BuildParsedSummary(CaptureEventRecord captureEvent, ParsedPush parsed, IReadOnlyList<CaptureImageRecord> images)
    {
        var summary = new ParsedSummary
        {
            CaptureId = captureEvent.CaptureId,
            CameraId = captureEvent.CameraId,
            StoreCode = captureEvent.StoreCode,
            ReceivedAtUtc = DateTime.SpecifyKind(captureEvent.ReceivedAtUtc, DateTimeKind.Utc),
            ParseStatus = captureEvent.ParseStatus.ToString(),
            ParseStatusCode = (byte)captureEvent.ParseStatus,
            ParseNote = captureEvent.ParseNote,
            EventFormat = parsed.EventFormat,
            EventType = captureEvent.EventType,
            EventState = captureEvent.EventState,
            CameraEventTime = captureEvent.CameraEventTime,
            CameraMac = captureEvent.CameraMac,
            CameraChannel = captureEvent.CameraChannel,
            IsRetransmission = captureEvent.IsRetransmission,
            FaceCount = captureEvent.FaceCount,
            BodyBytes = captureEvent.BodyBytes,
            BodySha256 = captureEvent.BodySha256,
            RawStorage = captureEvent.RawStorage,
            RawObjectKey = captureEvent.RawObjectKey,
            Images = images.Select(static image => new ParsedSummaryImage
            {
                ImageId = image.ImageId,
                Kind = image.Kind.ToString(),
                PartIndex = image.PartIndex,
                FaceIndex = image.FaceIndex,
                Storage = image.Storage,
                ObjectKey = image.ObjectKey,
                ContentType = image.ContentType,
                ByteSize = image.ByteSize,
                Sha256 = image.Sha256,
                Width = image.Width,
                Height = image.Height,
                RectX = image.RectX,
                RectY = image.RectY,
                RectW = image.RectW,
                RectH = image.RectH,
                RectNormalized = image.RectNormalized,
                FaceScore = image.FaceScore,
                CameraFaceId = image.CameraFaceId,
                AttributesJson = image.AttributesJson,
            }).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions);
    }
}

/// <summary><c>{name}.processed</c>：处理成功（终态）。</summary>
internal sealed record ProcessedMarker
{
    public int SchemaVersion { get; init; } = ProcessingMarkers.SchemaVersion;

    public required string CaptureId { get; init; }

    public required DateTimeOffset ProcessedAtUtc { get; init; }

    public required int Attempts { get; init; }

    public required string ParseStatus { get; init; }

    public required int FaceCount { get; init; }

    public required int ImageCount { get; init; }

    /// <summary>图片存储：local | cos。</summary>
    public required string ImageStorage { get; init; }

    /// <summary>原始报文存储：local | cos。</summary>
    public required string RawStorage { get; init; }

    /// <summary>是否已写库（false = 不连库模式，摘要见同名 .parsed.json）。</summary>
    public required bool DatabaseWritten { get; init; }

    public string? DuplicateOfCaptureId { get; init; }
}

/// <summary><c>{name}.retry</c>：处理失败、等待重试（非终态）。</summary>
internal sealed record RetryMarker
{
    public int SchemaVersion { get; init; } = ProcessingMarkers.SchemaVersion;

    public string? CaptureId { get; init; }

    /// <summary>已尝试次数（含刚失败的这次）。</summary>
    public int Attempts { get; init; }

    public DateTimeOffset LastAttemptUtc { get; init; }

    /// <summary>不早于此时间才会再次处理。</summary>
    public DateTimeOffset NextAttemptUtc { get; init; }

    /// <summary>最近一次错误摘要（截断 300 字）。</summary>
    public string? LastError { get; init; }
}

/// <summary><c>{name}.failed</c>：放弃处理（终态），原始报文保留到保留期。</summary>
internal sealed record FailedMarker
{
    public int SchemaVersion { get; init; } = ProcessingMarkers.SchemaVersion;

    public string? CaptureId { get; init; }

    public int Attempts { get; init; }

    public DateTimeOffset FailedAtUtc { get; init; }

    public string? LastError { get; init; }

    /// <summary>true = 元数据不可用、没有重试；false = 达到重试上限。</summary>
    public bool Permanent { get; init; }
}

/// <summary><c>{name}.parsed.json</c> 的内容。</summary>
internal sealed record ParsedSummary
{
    public int SchemaVersion { get; init; } = ProcessingMarkers.SchemaVersion;

    public required string CaptureId { get; init; }

    public required string CameraId { get; init; }

    public required string StoreCode { get; init; }

    public required DateTime ReceivedAtUtc { get; init; }

    public required string ParseStatus { get; init; }

    public required byte ParseStatusCode { get; init; }

    public string? ParseNote { get; init; }

    public string? EventFormat { get; init; }

    public string? EventType { get; init; }

    public string? EventState { get; init; }

    public DateTimeOffset? CameraEventTime { get; init; }

    public string? CameraMac { get; init; }

    public int? CameraChannel { get; init; }

    public bool? IsRetransmission { get; init; }

    public required int FaceCount { get; init; }

    public required long BodyBytes { get; init; }

    public required string BodySha256 { get; init; }

    public required string RawStorage { get; init; }

    public string? RawObjectKey { get; init; }

    public required IReadOnlyList<ParsedSummaryImage> Images { get; init; }
}

/// <summary>摘要里的一张图片（不含图片内容）。</summary>
internal sealed record ParsedSummaryImage
{
    public required string ImageId { get; init; }

    public required string Kind { get; init; }

    public required int PartIndex { get; init; }

    public int? FaceIndex { get; init; }

    public required string Storage { get; init; }

    public required string ObjectKey { get; init; }

    public string? ContentType { get; init; }

    public required int ByteSize { get; init; }

    public required string Sha256 { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public decimal? RectX { get; init; }

    public decimal? RectY { get; init; }

    public decimal? RectW { get; init; }

    public decimal? RectH { get; init; }

    public bool? RectNormalized { get; init; }

    public decimal? FaceScore { get; init; }

    public string? CameraFaceId { get; init; }

    public string? AttributesJson { get; init; }
}
