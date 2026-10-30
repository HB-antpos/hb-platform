namespace BlazorApp.Api.Services.Sms;

/// <summary>
/// 澳洲手机号规范化为 E.164（+614xxxxxxxx）。同一号码的不同写法必须落到同一个值，
/// 否则按号码的冷却与限流可以被「0412…」「+61 412…」换写法绕过。只接受手机号（04 开头），不接受座机。
/// </summary>
public static class AustralianPhoneNumber
{
    public static bool TryNormalize(string? raw, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        var hasPlus = text.StartsWith('+');
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        // 除数字外只允许常见分隔符，字母等一律拒绝，避免把「ext」之类拼进号码。
        if (text.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-' or '(' or ')' or '.' or '+'))) return false;
        if (text.Skip(1).Contains('+')) return false;

        string national; // 去掉国家码和前导 0 后的 9 位
        if (hasPlus || digits.StartsWith("0061"))
        {
            // 0061 是国际冠码 00 + 国家码 61：只去掉冠码，保留国家码，后面统一按 61 开头处理。
            var withoutPrefix = digits.StartsWith("0061") ? digits[2..] : digits;
            if (!withoutPrefix.StartsWith("61")) return false;
            national = withoutPrefix[2..];
            // 兼容 +61 0412… 这种多写了前导 0 的写法。
            if (national.StartsWith('0')) national = national[1..];
        }
        else if (digits.StartsWith("61") && digits.Length == 11)
        {
            national = digits[2..];
        }
        else if (digits.StartsWith('0'))
        {
            national = digits[1..];
        }
        else
        {
            return false;
        }

        if (national.Length != 9 || national[0] != '4') return false;
        e164 = "+61" + national;
        return true;
    }

    /// <summary>日志与界面用的脱敏号码：只保留国家码前缀和最后 3 位。</summary>
    public static string Mask(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164) || e164.Length < 7) return "***";
        return e164[..4] + new string('*', e164.Length - 7) + e164[^3..];
    }
}
