using System.Text.RegularExpressions;
using SixLabors.ImageSharp;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 存单与收据图片的类型、大小、对象键与内容校验。图片全程私有：先直传到 cash/pending/，
/// 提交存款或支出时校验通过才转正到 cash/{分店}/{年月}/ 下的正式私有路径。
/// </summary>
public static class StoreCashImageRules
{
    public const long MaximumFileSize = 5 * 1024 * 1024;

    /// <summary>存单照片要能看清数字，边长上限比证件照宽松；手机端会先压到长边 2048。</summary>
    private const int MaximumEdge = 4096;

    private const string PendingPrefix = "cash/pending/";

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    public static bool IsValid(string? contentType, long fileSize) =>
        !string.IsNullOrWhiteSpace(contentType)
        && Extensions.ContainsKey(contentType.Trim())
        && fileSize > 0
        && fileSize <= MaximumFileSize;

    public static string BuildPendingObjectKey(string storeCode, string token, string contentType) =>
        $"{PendingPrefix}{SafeSegment(storeCode)}/{token}{Extensions[contentType.Trim()]}";

    public static string BuildFinalObjectKey(string storeCode, DateTime utcNow, string token, string contentType) =>
        $"cash/{SafeSegment(storeCode)}/{utcNow:yyyyMM}/{token}{Extensions[contentType.Trim()]}";

    /// <summary>待确认对象键必须在本店的 pending 目录下，防止拿别店或别的业务的对象来冒充。</summary>
    public static bool OwnsPendingObjectKey(string? objectKey, string storeCode) =>
        !string.IsNullOrWhiteSpace(objectKey)
        && objectKey.StartsWith($"{PendingPrefix}{SafeSegment(storeCode)}/", StringComparison.Ordinal);

    /// <summary>实际对象与签发时声明的一致：大小、类型、归属账号都对得上。</summary>
    public static bool MatchesMetadata(
        string userGuid,
        long expectedSize,
        string expectedContentType,
        long? actualSize,
        string? actualContentType,
        string? owner,
        string? kind,
        long? declaredSize,
        string? declaredContentType
    ) =>
        actualSize == expectedSize
        && declaredSize == expectedSize
        && string.Equals(actualContentType, expectedContentType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(declaredContentType, expectedContentType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(owner, userGuid, StringComparison.Ordinal)
        && string.Equals(kind, "cash", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesImageContent(byte[] bytes, string contentType)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            var info = Image.Identify(bytes, out var identifiedFormat);
            if (
                info is null
                || info.Width <= 0
                || info.Height <= 0
                || info.Width > MaximumEdge
                || info.Height > MaximumEdge
                || !string.Equals(identifiedFormat.DefaultMimeType, contentType, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }

            // Identify 只看头部，仍需完整解码一次，拒绝伪造 magic 或被截断的图片。
            using var image = Image.Load(bytes, out var decodedFormat);
            return string.Equals(decodedFormat.DefaultMimeType, contentType, StringComparison.OrdinalIgnoreCase);
        }
        catch (UnknownImageFormatException)
        {
            return false;
        }
        catch (InvalidImageContentException)
        {
            return false;
        }
    }

    private static string SafeSegment(string value) => Regex.Replace(value, "[^A-Za-z0-9_-]", string.Empty);
}
