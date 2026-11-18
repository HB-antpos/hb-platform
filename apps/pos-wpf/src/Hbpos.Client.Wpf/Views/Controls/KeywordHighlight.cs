using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 给 <see cref="TextBlock"/> 加「关键词命中高亮」的附加属性：
/// 把 <see cref="TextProperty"/> 按 <see cref="KeywordProperty"/> 切成若干 Run，命中的片段加底色并加粗。
/// 用在历史页列表的收银员 / 终端文字上，让收银员一眼看出这行是被哪个关键词筛出来的。
/// 关键词为空或没命中时退回普通文字，行为与直接绑 Text 一致。
/// </summary>
public static class KeywordHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(KeywordHighlight),
        new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty KeywordProperty = DependencyProperty.RegisterAttached(
        "Keyword",
        typeof(string),
        typeof(KeywordHighlight),
        new PropertyMetadata(null, OnInputChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetKeyword(DependencyObject element) => (string?)element.GetValue(KeywordProperty);

    public static void SetKeyword(DependencyObject element, string? value) => element.SetValue(KeywordProperty, value);

    private static void OnInputChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock textBlock)
        {
            return;
        }

        Rebuild(textBlock, GetText(textBlock), GetKeyword(textBlock));
    }

    /// <summary>
    /// 切分文本：忽略大小写匹配关键词（与查询端 OrdinalIgnoreCase 一致），命中片段用原文大小写显示。
    /// 公开成纯函数，便于不依赖 WPF 控件做单元测试。
    /// </summary>
    public static IReadOnlyList<(string Text, bool IsMatch)> Split(string? text, string? keyword)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [(text, false)];
        }

        var needle = keyword.Trim();
        var parts = new List<(string, bool)>();
        var index = 0;
        while (index < text.Length)
        {
            var hit = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                parts.Add((text[index..], false));
                break;
            }

            if (hit > index)
            {
                parts.Add((text[index..hit], false));
            }

            parts.Add((text.Substring(hit, needle.Length), true));
            index = hit + needle.Length;
        }

        return parts;
    }

    private static void Rebuild(TextBlock textBlock, string? text, string? keyword)
    {
        textBlock.Inlines.Clear();
        var parts = Split(text, keyword);
        if (parts.Count == 0)
        {
            return;
        }

        // 取主题里的警示色做底色（8 套主题都有）；资源缺失时退回固定的浅黄，保证一定看得见。
        var highlightBrush = textBlock.TryFindResource("PosWarningBorderBrush") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x8A));
        foreach (var (part, isMatch) in parts)
        {
            var run = new Run(part);
            if (isMatch)
            {
                run.Background = highlightBrush;
                run.FontWeight = FontWeights.Bold;
            }

            textBlock.Inlines.Add(run);
        }
    }
}
