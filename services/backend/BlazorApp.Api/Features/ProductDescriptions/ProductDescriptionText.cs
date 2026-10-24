using System.Text.RegularExpressions;

namespace BlazorApp.Api.Features.ProductDescriptions;

/// <summary>
/// 英文商品名清洗：模型输出常多带引号、换行或句号，这里统一收口，并按单词边界硬卡长度。
/// </summary>
public static class ProductDescriptionText
{
    private static readonly Regex ChineseRegex = new(@"[㐀-鿿]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LetterRegex = new(@"[A-Za-z]", RegexOptions.Compiled);

    private static readonly char[] WrappingChars = { '"', '\'', '`', '“', '”', '‘', '’', '「', '」' };
    private static readonly char[] TrailingChars = { '.', ',', ';', ':', '!', '?', '-', '–', '—', '/', '&', '、', '。', '，', ' ' };

    // 截断后落在末尾会读不通的连接词，例如 "Ceramic Mug With" -> "Ceramic Mug"。
    private static readonly HashSet<string> DanglingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "with", "for", "of", "in", "on", "or", "to", "the", "a", "an", "&", "+",
    };

    /// <summary>返回清洗后的英文名；为空、含中文或没有英文字母时返回 null（视为无效结果）。</summary>
    public static string? Normalize(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = WhitespaceRegex.Replace(raw, " ").Trim().Trim(WrappingChars).Trim();
        text = text.TrimEnd(TrailingChars);

        if (text.Length == 0 || ChineseRegex.IsMatch(text) || !LetterRegex.IsMatch(text))
        {
            return null;
        }

        if (maxLength > 0 && text.Length > maxLength)
        {
            text = TruncateAtWordBoundary(text, maxLength);
        }

        return text.Length == 0 ? null : text;
    }

    private static string TruncateAtWordBoundary(string text, int maxLength)
    {
        // 优先在 maxLength 以内最后一个空格处截断；整段没有空格才硬切，保证结果不超长。
        var cut = text[..maxLength];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > 0 && text[maxLength] != ' ')
        {
            cut = cut[..lastSpace];
        }

        var words = cut.TrimEnd(TrailingChars).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && DanglingWords.Contains(words[^1].TrimEnd(TrailingChars)))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words).TrimEnd(TrailingChars);
    }
}
