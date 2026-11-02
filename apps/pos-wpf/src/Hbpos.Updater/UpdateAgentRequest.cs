using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hbpos.Updater;

/// <summary>
/// 更新窗口（普通用户）请求管理员更新代理安装的内容。代理以 SYSTEM 运行，任何本机程序都能发这个请求，
/// 所以这里的每个字段都按不可信输入逐项校验：只接受公司腾讯云 COS 发布目录里的安装包。
/// </summary>
internal sealed record UpdateAgentRequest(string RequestId, Uri DownloadUrl, string Sha256, long FileSize)
{
    internal const long MaxInstallerBytes = 512L * 1024 * 1024;
    internal const int MaxEncodedLength = 4096;

    // 腾讯云 COS 存储桶名固定以账号 APPID 结尾，只有公司账号能在这些桶里写对象；
    // WPF 安装包由后台“WPF 版本”页上传到 wpf-releases/ 目录。
    private const string TrustedCosAppId = "1300114625";
    private const string TrustedPathPrefix = "/wpf-releases/";

    private static readonly Regex TrustedHostPattern = new(
        $@"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?-{TrustedCosAppId}\.cos\.[a-z0-9-]+\.myqcloud\.com$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SafePathPattern = new(
        @"^[A-Za-z0-9._/-]+$",
        RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool TryCreate(
        string? requestId,
        string? downloadUrl,
        string? sha256,
        long? fileSize,
        out UpdateAgentRequest? request,
        out string error)
    {
        request = null;
        if (!Guid.TryParseExact(requestId, "N", out var parsedId))
        {
            error = "invalid-request-id";
            return false;
        }

        if (!TryValidateDownloadUrl(downloadUrl, out var uri, out error))
        {
            return false;
        }

        var normalizedSha256 = sha256?.Trim().ToLowerInvariant();
        if (normalizedSha256 is not { Length: 64 } || !normalizedSha256.All(Uri.IsHexDigit))
        {
            error = "invalid-sha256";
            return false;
        }

        if (fileSize is not (> 0 and <= MaxInstallerBytes))
        {
            error = "invalid-file-size";
            return false;
        }

        request = new UpdateAgentRequest(parsedId.ToString("N"), uri, normalizedSha256, fileSize.Value);
        error = string.Empty;
        return true;
    }

    public string Encode()
    {
        var json = JsonSerializer.Serialize(
            new Payload(1, RequestId, DownloadUrl.AbsoluteUri, Sha256, FileSize),
            JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? encoded, out UpdateAgentRequest? request, out string error)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxEncodedLength)
        {
            error = "invalid-encoding";
            return false;
        }

        Payload? payload;
        try
        {
            var base64 = encoded.Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(base64), JsonOptions);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            error = "invalid-encoding";
            return false;
        }

        if (payload is not { Version: 1 })
        {
            error = "unsupported-version";
            return false;
        }

        return TryCreate(payload.Id, payload.Url, payload.Sha256, payload.Size, out request, out error);
    }

    internal static bool TryValidateDownloadUrl(string? downloadUrl, out Uri uri, out string error)
    {
        uri = null!;
        if (!Uri.TryCreate(downloadUrl?.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "invalid-url";
            return false;
        }

        // 中文注释：只认 HTTPS 默认端口的 COS 源站地址；带账号、查询串、非发布目录或非 exe 的地址一律拒绝。
        if (parsed.Scheme != Uri.UriSchemeHttps ||
            !parsed.IsDefaultPort ||
            parsed.UserInfo.Length > 0 ||
            parsed.Query.Length > 0 ||
            parsed.Fragment.Length > 0 ||
            !TrustedHostPattern.IsMatch(parsed.IdnHost))
        {
            error = "untrusted-url";
            return false;
        }

        var path = parsed.AbsolutePath;
        if (!path.StartsWith(TrustedPathPrefix, StringComparison.Ordinal) ||
            !SafePathPattern.IsMatch(path) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains("//", StringComparison.Ordinal) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "untrusted-url";
            return false;
        }

        uri = parsed;
        error = string.Empty;
        return true;
    }

    public override string ToString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"id={RequestId} url={DownloadUrl.AbsoluteUri} size={FileSize}");
    }

    private sealed record Payload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("size")] long? Size);
}
