using System.Globalization;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 落盘文件命名约定（P1 解析与运维脚本都依赖它，改动需同步）：
/// <c>raw/{yyyy-MM-dd}/{cameraId}/{yyyyMMdd'T'HHmmssfff'Z'}_{guid:N}.bin</c> 与同名 <c>.json</c>；
/// 写入过程中的临时文件为 <c>.bin.tmp</c> / <c>.json.tmp</c>。日期与时间戳都取服务端 UTC 接收时间。
/// </summary>
internal static class CaptureFileNames
{
    public const string RawDirectoryName = "raw";
    public const string BodyExtension = ".bin";
    public const string MetadataExtension = ".json";
    public const string TempExtension = ".tmp";

    private const string DateDirectoryFormat = "yyyy-MM-dd";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const int TimestampLength = 19;

    /// <summary>日期目录名（UTC）。</summary>
    public static string FormatDateDirectory(DateTimeOffset receivedAtUtc)
        => receivedAtUtc.UtcDateTime.ToString(DateDirectoryFormat, CultureInfo.InvariantCulture);

    /// <summary>不带扩展名的文件名：UTC 毫秒时间戳 + 无连字符 GUID。</summary>
    public static string FormatBaseName(DateTimeOffset receivedAtUtc, string captureId)
        => receivedAtUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "_" + captureId;

    /// <summary>判断文件属于哪一类；不是本服务产生的文件返回 <see cref="CaptureFileKind.Other"/>。</summary>
    public static CaptureFileKind Classify(string fileName)
    {
        if (fileName.EndsWith(TempExtension, StringComparison.Ordinal))
        {
            return CaptureFileKind.Temp;
        }

        if (fileName.EndsWith(BodyExtension, StringComparison.Ordinal))
        {
            return CaptureFileKind.Body;
        }

        return fileName.EndsWith(MetadataExtension, StringComparison.Ordinal)
            ? CaptureFileKind.Metadata
            : CaptureFileKind.Other;
    }

    /// <summary>从文件名前缀解析接收时间（UTC）；不是本服务的命名格式返回 null。</summary>
    public static DateTimeOffset? TryParseReceivedAt(string fileName)
    {
        if (fileName.Length < TimestampLength)
        {
            return null;
        }

        return DateTime.TryParseExact(
            fileName.AsSpan(0, TimestampLength),
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }

    /// <summary>解析日期目录名；格式不对返回 null。</summary>
    public static DateOnly? TryParseDateDirectory(string directoryName)
        => DateOnly.TryParseExact(directoryName, DateDirectoryFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}

/// <summary>落盘文件类别。</summary>
internal enum CaptureFileKind
{
    /// <summary>非本服务文件，清理与统计都忽略。</summary>
    Other,

    /// <summary>原始请求体 .bin。</summary>
    Body,

    /// <summary>元数据 .json。</summary>
    Metadata,

    /// <summary>写入中的临时文件 .bin.tmp / .json.tmp。</summary>
    Temp,
}
