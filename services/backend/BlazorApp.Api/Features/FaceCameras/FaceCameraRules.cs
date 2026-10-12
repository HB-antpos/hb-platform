using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlazorApp.Api.Features.FaceCameras;

/// <summary>
/// 摄像机字段规则与 token 生成：与人脸接收服务（BlazorApp.FaceIngest）及 docs/runbooks/face-ingest.md 第 4 节保持一致。
/// </summary>
internal static class FaceCameraRules
{
    public const int MaxCameraIdLength = 64;
    public const int MaxStoreCodeLength = 32;
    public const int MaxNoteLength = 200;
    public const int MaxDeviceModelLength = 64;
    public const int MaxMacAddressLength = 32;

    /// <summary>24 字节随机数 → 48 位十六进制，摄像机 URL 字段（上限 128）里路径部分共 68 字符。</summary>
    public const int TokenByteLength = 24;

    public const string IngestPathPrefix = "/face-ingest/hik/v1/";

    // CameraId 会进文件路径：用 \z 而不是 $（$ 会放过结尾换行）；小写字母数字横线，首字符不能是横线。
    private static readonly Regex CameraIdPattern = new(
        @"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StoreCodePattern = new(
        @"^[A-Za-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValidCameraId(string? value) => value != null && CameraIdPattern.IsMatch(value);

    public static bool IsValidStoreCode(string? value) => value != null && StoreCodePattern.IsMatch(value);

    /// <summary>可选文本：trim，空串视为 null；超长或含控制字符返回 false（防止日志注入与存储脏数据）。</summary>
    public static bool TryNormalizeOptionalText(string? value, int maxLength, out string? normalized)
    {
        normalized = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (text.Length > maxLength || text.Any(char.IsControl))
        {
            return false;
        }

        normalized = text;
        return true;
    }

    /// <summary>密码学安全随机数生成 token（48 位小写十六进制）。</summary>
    public static string GenerateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenByteLength));

    /// <summary>token 的小写 SHA-256 十六进制；编码方式与人脸接收服务（UTF-8）一致。</summary>
    public static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string BuildCameraUrl(string publicBaseUrl, string token) =>
        publicBaseUrl.TrimEnd('/') + IngestPathPrefix + token;
}
