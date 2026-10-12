using System.Text;
using Microsoft.Extensions.Primitives;

namespace BlazorApp.FaceIngest;

/// <summary>token 从请求的哪个位置取到。</summary>
internal enum TokenSource
{
    None,
    Path,
    Query,
    Basic,
}

/// <summary>从请求中解析出的候选 token（尚未校验）。</summary>
internal readonly record struct ResolvedToken(string? Value, TokenSource Source)
{
    /// <summary>写进元数据 / 日志的来源名：path | query | basic | none。</summary>
    public string SourceName => Source switch
    {
        TokenSource.Path => "path",
        TokenSource.Query => "query",
        TokenSource.Basic => "basic",
        _ => "none",
    };
}

/// <summary>
/// 按固定优先级取摄像机 token：路径 <c>/hik/v1/{token}</c> → query <c>token=</c> → <c>Authorization: Basic</c> 的 password。
/// 只取第一个存在的位置，不在多个位置间回退尝试；Digest 不支持。
/// </summary>
internal static class IngestTokenResolver
{
    /// <summary>路由模板里的 token 参数名。</summary>
    public const string RouteValueName = "token";

    /// <summary>query 里的 token 参数名（取值时大小写不敏感，与 ASP.NET Core 的 Query 集合一致）。</summary>
    public const string QueryParameterName = "token";

    // 中文注释：Authorization 头再长也不可能是合法摄像机凭据（海康 password 最长 16 位），超长直接忽略，避免无谓的解码分配。
    private const int MaxBasicCredentialLength = 1024;

    public static ResolvedToken Resolve(HttpRequest request)
    {
        if (request.RouteValues.TryGetValue(RouteValueName, out var routeValue) && routeValue is string pathToken)
        {
            // 中文注释：部分固件会把 &macAddress=… 直接拼在 URL 末尾（URL 本身没有 ? 时也一样），
            // 这时路径段会变成 "<token>&macAddress=…"；token 本身不会含 &，截掉后半段再比对。
            var ampersand = pathToken.IndexOf('&', StringComparison.Ordinal);
            if (ampersand >= 0)
            {
                pathToken = pathToken[..ampersand];
            }

            if (pathToken.Length > 0)
            {
                return new ResolvedToken(pathToken, TokenSource.Path);
            }
        }

        foreach (var queryToken in request.Query[QueryParameterName])
        {
            if (!string.IsNullOrEmpty(queryToken))
            {
                return new ResolvedToken(queryToken, TokenSource.Query);
            }
        }

        var basicPassword = TryReadBasicPassword(request.Headers.Authorization);
        if (!string.IsNullOrEmpty(basicPassword))
        {
            return new ResolvedToken(basicPassword, TokenSource.Basic);
        }

        return new ResolvedToken(null, TokenSource.None);
    }

    private static string? TryReadBasicPassword(StringValues authorization)
    {
        foreach (var header in authorization)
        {
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            var value = header.AsSpan().Trim();
            if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var encoded = value[6..].Trim();
            if (encoded.Length == 0 || encoded.Length > MaxBasicCredentialLength)
            {
                return null;
            }

            var decodedBytes = new byte[encoded.Length];
            if (!Convert.TryFromBase64Chars(encoded, decodedBytes, out var written))
            {
                return null;
            }

            var credentials = Encoding.UTF8.GetString(decodedBytes, 0, written);
            var colon = credentials.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? null : credentials[(colon + 1)..];
        }

        return null;
    }
}
