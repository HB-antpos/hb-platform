using System.Text.RegularExpressions;

namespace BlazorApp.Api.Services.React.HolidaySalesReport;

/// <summary>
/// 节日销售报告的大类归并：把 KFC / Brazco 网站上细碎的分类（KFC 约 60 个、Brazco 约 30 个）合成 7 个大类。
/// 规则按顺序命中第一条，顺序本身就是业务口径，改动前先看分类对照页的效果。
/// </summary>
internal static class HolidaySalesCategoryRules
{
    internal const string Costume = "Costume";
    internal const string Decoration = "Decoration";
    internal const string MakeUp = "MakeUp";
    internal const string Accessory = "Accessory";
    internal const string Party = "Party";
    internal const string MaskWig = "MaskWig";
    internal const string Animated = "Animated";

    /// <summary>全部大类，页面按销售额重新排序，这里的顺序只用于没有销售时的兜底。</summary>
    internal static readonly string[] AllKeys = [Costume, Decoration, MakeUp, Accessory, Party, MaskWig, Animated];

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>供应商分类名 → 大类。服装排在最前：「Dress Up &amp; Accessories」这类含配件字样的分类仍然主要是服装。</summary>
    private static readonly (string Key, Regex Pattern)[] SupplierCategoryRules =
    [
        (Costume, Rx(@"costume|dress up|clothing")),
        (MaskWig, Rx(@"mask|\bwigs?\b|headband|\bhats?\b")),
        (MakeUp, Rx(@"make ?-?up|blood|tattoo|special effect")),
        (Animated, Rx(@"animat|light up|projector|inflatable|glimmer")),
        (Party, Rx(@"party|tableware|trick or treat|kids fun|halloween fun|novelty|glowstick|bucket")),
        (Decoration, Rx(@"decoration|indoor|hanging|door|tinsel|flower|skeleton|skull|lawn|creepy crawl|creature|homeware|\bsigns?\b")),
        (Accessory, Rx(@"accessor|weapon|asotv|pets?$|stocking")),
    ];

    /// <summary>
    /// 商品名 → 大类，只用于供应商分类说明不了的商品：
    /// KFC 的主题系列（Witch / Vampire / Zombie / Creepy Clown …）里服装、装饰、道具都有，分类名本身不能代表商品类型。
    /// </summary>
    private static readonly (string Key, Regex Pattern)[] ProductNameRules =
    [
        (Costume, Rx(@"costume|\bdress|\bcape\b|\brobe\b|\btutu\b|jumpsuit|onesie")),
        (MaskWig, Rx(@"mask|\bwigs?\b|\bhats?\b|headband|\bhorns\b|\bcrown\b|tiara|\bhood\b")),
        (MakeUp, Rx(@"make ?up|blood|paint|tattoo|\bnails?\b|teeth|\bscars?\b|wound")),
        (Animated, Rx(@"animat|light|\bled\b|b/op|projector|inflat|sound")),
        (Party, Rx(@"bucket|\bbag|plate|\bcups?\b|napkin|party|lolly|candy|treat|balloon")),
        (Decoration, Rx(@"hanging|decor|\bsigns?\b|garland|skeleton|skull|spider|\bweb|tombstone|banner|wreath|statue|\bprops?\b|cage|\bbones?\b")),
    ];

    /// <summary>
    /// 解析商品所属大类。优先级：品名含 Costume 一律是服装 → 供应商分类名关键词 → 品名关键词 → 配件·道具兜底。
    /// </summary>
    internal static string Resolve(string? supplierCategory, string? productName)
    {
        if (productName?.Contains("costume", StringComparison.OrdinalIgnoreCase) == true)
            return Costume;

        if (!string.IsNullOrWhiteSpace(supplierCategory))
        {
            foreach (var (key, pattern) in SupplierCategoryRules)
            {
                if (pattern.IsMatch(supplierCategory))
                    return key;
            }
        }

        if (!string.IsNullOrWhiteSpace(productName))
        {
            foreach (var (key, pattern) in ProductNameRules)
            {
                if (pattern.IsMatch(productName))
                    return key;
            }
        }

        return Accessory;
    }
}
