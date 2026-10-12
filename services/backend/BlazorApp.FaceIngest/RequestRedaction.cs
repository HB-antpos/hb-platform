namespace BlazorApp.FaceIngest;

/// <summary>
/// 元数据打码规则：任何落盘文件都不得出现 token 原文。
/// - Authorization / Cookie / Proxy-Authorization 的值整体替换为 <c>***</c>；
/// - query 里名为 token 的参数值替换为 <c>***</c>，其余参数原样；
/// - 兜底：path、query、所有请求头值里出现的 token 原文（及其 URL 编码形式）一律替换为 <c>***</c>。
/// </summary>
internal static class RequestRedaction
{
    public const string Mask = "***";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Proxy-Authorization",
    };

    /// <summary>是否属于整体打码的敏感请求头。</summary>
    public static bool IsSensitiveHeader(string name) => SensitiveHeaders.Contains(name);

    /// <summary>把字符串里出现的 token 原文与其 URL 编码形式替换为 <c>***</c>。</summary>
    public static string? RedactToken(string? value, string? token)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(token))
        {
            return value;
        }

        var redacted = value.Replace(token, Mask, StringComparison.Ordinal);
        var escaped = Uri.EscapeDataString(token);
        if (!string.Equals(escaped, token, StringComparison.Ordinal))
        {
            redacted = redacted.Replace(escaped, Mask, StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }

    /// <summary>
    /// query 打码：保持原始编码与参数顺序，只把参数名（解码后、大小写不敏感）为 token 的值替换为 <c>***</c>。
    /// 输入为 <see cref="QueryString.Value"/>（含开头的 ? 或为空）。
    /// </summary>
    public static string RedactQuery(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
        {
            return string.Empty;
        }

        var hasQuestionMark = rawQuery[0] == '?';
        var parts = (hasQuestionMark ? rawQuery[1..] : rawQuery).Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var rawKey = equals >= 0 ? part[..equals] : part;
            if (equals >= 0 && IsTokenKey(rawKey))
            {
                parts[i] = rawKey + "=" + Mask;
            }
        }

        return (hasQuestionMark ? "?" : string.Empty) + string.Join('&', parts);
    }

    private static bool IsTokenKey(string rawKey)
    {
        var decoded = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
        return string.Equals(decoded, IngestTokenResolver.QueryParameterName, StringComparison.OrdinalIgnoreCase);
    }
}
