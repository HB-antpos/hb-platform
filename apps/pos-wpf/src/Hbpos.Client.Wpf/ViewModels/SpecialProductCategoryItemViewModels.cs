using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 分类栏里的一行：「全部」、某个真实分类或「未分类」。
/// Id 为 null = 全部，<see cref="Services.SpecialProductCategorySnapshot.UncategorizedId"/> = 未分类，其余是服务器分类 Id。
/// </summary>
public sealed partial class SpecialCategoryEntryViewModel : ObservableObject
{
    private static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [ObservableProperty]
    private bool _isSelected;

    public SpecialCategoryEntryViewModel(string? id, string name, string? color, int count, bool isAll)
    {
        Id = id;
        Name = name;
        ColorHex = NormalizeColor(color);
        Count = count;
        IsAll = isAll;
    }

    /// <summary>颜色来自服务端自由文本，只认 #RRGGBB，其余一律当没有颜色（色点退回中性灰），避免坏值让绑定报错。</summary>
    public static string? NormalizeColor(string? color) =>
        color is not null && HexColor.IsMatch(color.Trim()) ? color.Trim() : null;

    public string? Id { get; }

    public string Name { get; }

    public string? ColorHex { get; }

    public bool HasColor => ColorHex is not null;

    public int Count { get; }

    public string CountText => Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>「全部」行用图标代替色点。</summary>
    public bool IsAll { get; }

    public bool ShowsDot => !IsAll;
}

/// <summary>「移到分类」弹层里的一个选项：某个分类，或 CategoryId 为 null 的「未分类」。</summary>
public sealed record SpecialCategoryChoice(string? CategoryId, string Name, string? ColorHex, bool IsCurrent)
{
    public bool HasColor => ColorHex is not null;
}
