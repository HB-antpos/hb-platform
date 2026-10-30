using System.Security.Cryptography;
using System.Text;

namespace BlazorApp.Api.Features.PosReceipts;

/// <summary>
/// 在线小票分享令牌：12 位 Crockford Base32（去掉易混的 I L O U），共 60 位随机熵，二维码里链接短、扫得快。
/// 防猜靠三层：随机熵、按 IP 限流、库里只存哈希；令牌无效 / 过期 / 撤销对外一律同一种 404。
/// </summary>
public static class PosReceiptShareTokens
{
    public const int Length = 12;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        // 32 个符号恰好是 5 位，取随机字节低 5 位无取模偏差。
        Span<byte> bytes = stackalloc byte[Length];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[Length];
        for (var index = 0; index < Length; index++)
        {
            chars[index] = Alphabet[bytes[index] & 0x1F];
        }

        return new string(chars);
    }

    /// <summary>
    /// 规范化访客传来的令牌：去空白、转大写、必须恰好 12 位且全在字母表内；不合法直接当作不存在，不去查库。
    /// </summary>
    public static bool TryNormalize(string? raw, out string token)
    {
        token = string.Empty;
        var trimmed = raw?.Trim().ToUpperInvariant();
        if (trimmed is null || trimmed.Length != Length)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!Alphabet.Contains(character))
            {
                return false;
            }
        }

        token = trimmed;
        return true;
    }

    /// <summary>SHA-256(令牌) 的小写十六进制，64 字符。</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>公开链接：基址 + /r/ + 令牌。基址已由 <see cref="PosReceiptOptions.ResolveBaseUrl"/> 规范化。</summary>
    public static string BuildUrl(string baseUrl, string token) => $"{baseUrl.TrimEnd('/')}/r/{token}";
}
