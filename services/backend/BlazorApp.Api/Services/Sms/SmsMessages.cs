using System.Globalization;
using System.Text;

namespace BlazorApp.Api.Services.Sms;

/// <summary>
/// 所有短信文案集中在这里，只有英文。
/// 短信编码有硬约束：只要出现一个中文、弯引号、长破折号或 emoji，整条就会改用 UCS-2 编码，
/// 单条上限从 160 字符降到 70，费用翻倍且长链接容易被拆开。所以这里只允许一个保守的 ASCII 子集，
/// 模板里要代入的姓名等自由文本必须先过 <see cref="SafeName"/>，过不了就用兜底词，宁可不写名字也不破坏编码。
/// </summary>
public static class SmsMessages
{
    public const string Brand = "Hot Bargain";
    public const int SingleSegmentLength = 160;
    public const int MultiSegmentLength = 153;

    /// <summary>通用一次性验证码短信。</summary>
    public static string VerificationCode(string code, int validMinutes) =>
        $"{Brand}: your verification code is {code}. It expires in {validMinutes} minutes. Do not share this code with anyone.";

    /// <summary>监护人签署验证码：额外强调不能告诉员工本人，防止代签。</summary>
    public static string GuardianVerificationCode(string code, int validMinutes) =>
        $"{Brand}: your verification code is {code}. It expires in {validMinutes} minutes. Do not share this code with anyone, including the employee.";

    /// <summary>监护人签署链接。<paramref name="validUntilText"/> 是已带时区的有效期文字，如 05/11 14:00 Sydney time。</summary>
    public static string GuardianSigningLink(string childName, string signingUrl, string validUntilText) =>
        $"{Brand}: please review and sign the consent form for {SafeName(childName, "your child")}: {signingUrl} Valid until {validUntilText}.";

    /// <summary>
    /// 姓名进短信前的净化：去掉变音符号（José → Jose），过滤后仍含非允许字符（如中文名）就用兜底词。
    /// </summary>
    public static string SafeName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c);
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length == 0 || cleaned.Length > 40 || !IsGsm7Safe(cleaned)) return fallback;
        return cleaned;
    }

    /// <summary>
    /// 是否只含「保守 GSM-7 子集」：字母、数字、空格、换行和一组常见标点。
    /// 刻意不收 \ ^ { } [ ] ~ | € 这些在 GSM-7 里要占两个字符位的扩展字符，也不收任何非 ASCII 字符。
    /// </summary>
    public static bool IsGsm7Safe(string text)
    {
        foreach (var c in text)
        {
            var ok = char.IsAsciiLetterOrDigit(c)
                || c is ' ' or '\n' or '!' or '"' or '#' or '%' or '&' or '\'' or '(' or ')' or '*' or '+'
                    or ',' or '-' or '.' or '/' or ':' or ';' or '<' or '=' or '>' or '?' or '@' or '_' or '$';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>计费条数：不超过 160 字符算 1 条，超出后每条按 153 字符拼接。</summary>
    public static int SegmentCount(string text) =>
        text.Length <= SingleSegmentLength ? 1 : (text.Length + MultiSegmentLength - 1) / MultiSegmentLength;
}
