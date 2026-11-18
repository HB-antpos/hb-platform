using Hbpos.Client.Wpf.Views.Controls;

namespace Hbpos.Client.Tests;

/// <summary>历史页行内「关键词命中高亮」的切分规则（纯函数，不依赖 WPF 控件）。</summary>
public sealed class KeywordHighlightTests
{
    [Theory]
    [InlineData(null, "a")]
    [InlineData("", "a")]
    public void Empty_text_yields_no_parts(string? text, string keyword)
    {
        Assert.Empty(KeywordHighlight.Split(text, keyword));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_keyword_yields_single_plain_part(string? keyword)
    {
        var parts = KeywordHighlight.Split("Amy", keyword);

        Assert.Equal([("Amy", false)], parts);
    }

    [Fact]
    public void Match_is_case_insensitive_and_keeps_original_casing()
    {
        var parts = KeywordHighlight.Split("Amy · POS_1012", "amy");

        Assert.Equal([("Amy", true), (" · POS_1012", false)], parts);
    }

    [Fact]
    public void Keyword_is_trimmed_and_every_occurrence_is_marked()
    {
        var parts = KeywordHighlight.Split("1012-x-1012", "  1012 ");

        Assert.Equal([("1012", true), ("-x-", false), ("1012", true)], parts);
    }

    [Fact]
    public void No_match_keeps_whole_text_as_plain()
    {
        var parts = KeywordHighlight.Split("Leo", "Amy");

        Assert.Equal([("Leo", false)], parts);
    }
}
