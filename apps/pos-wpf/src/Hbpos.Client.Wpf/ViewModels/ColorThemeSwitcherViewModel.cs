using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>标题栏的配色切换菜单：只负责展示选项与转发选择，切换与保存由 IColorThemeService 完成。</summary>
public sealed partial class ColorThemeSwitcherViewModel : ObservableObject
{
    private readonly IColorThemeService _colorThemeService;
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    private bool _isMenuOpen;

    public ColorThemeSwitcherViewModel(IColorThemeService colorThemeService, ILocalizationService localization)
    {
        _colorThemeService = colorThemeService;
        _localization = localization;
        Options = new ObservableCollection<ColorThemeOption>(
            Definitions.Select(definition => new ColorThemeOption(definition.Theme, definition.Swatches)));
        SelectCommand = new AsyncRelayCommand<PosColorTheme>(SelectAsync);
        _colorThemeService.ThemeChanged += (_, _) => RefreshSelection();
        _localization.CultureChanged += (_, _) => RefreshText();
        RefreshText();
        RefreshSelection();
    }

    public ObservableCollection<ColorThemeOption> Options { get; }

    public IAsyncRelayCommand<PosColorTheme> SelectCommand { get; }

    public string MenuTitle { get; private set; } = string.Empty;

    // 标题栏按钮上显示的当前配色名称。
    public string CurrentThemeName => Options.FirstOrDefault(option => option.IsSelected)?.Name ?? string.Empty;

    // 预览色块：背景、主色、重点金额，与 Themes/Palettes 中的色板一致。
    internal static IReadOnlyList<(PosColorTheme Theme, string[] Swatches)> Definitions { get; } =
    [
        (PosColorTheme.Blue, ["#FFF3F5F8", "#FF0B5ED7", "#FFF59E0B"]),
        // 彩色主题：背景、主色、第三块取该主题最有代表性的点缀色（键盘区 / 功能键）
        (PosColorTheme.Candy, ["#FFF4F2FF", "#FF7C3AED", "#FFEA580C"]),
        (PosColorTheme.Sunny, ["#FFFFF8EC", "#FFC2410C", "#FF0F766E"]),
        (PosColorTheme.Ocean, ["#FFEEFAFC", "#FF0E7490", "#FFE11D48"]),
        (PosColorTheme.Rainbow, ["#FFF5F8FF", "#FF1D4ED8", "#FF7C3AED"]),
        (PosColorTheme.Graphite, ["#FF14171B", "#FF2F76B8", "#FFEFC05A"]),
        (PosColorTheme.Celadon, ["#FFE1E8E3", "#FF245A74", "#FFF0E2A2"]),
        // 长者模式：购物车灰白、深蓝、支付绿
        (PosColorTheme.Elder, ["#FFE9EDF2", "#FF1E3A8A", "#FF05652A"])
    ];

    private async Task SelectAsync(PosColorTheme theme)
    {
        IsMenuOpen = false;
        await _colorThemeService.SelectAsync(theme);
    }

    private void RefreshSelection()
    {
        foreach (var option in Options)
        {
            option.IsSelected = option.Theme == _colorThemeService.Current;
        }

        OnPropertyChanged(nameof(CurrentThemeName));
    }

    private void RefreshText()
    {
        MenuTitle = _localization.T("shell.colorTheme");
        OnPropertyChanged(nameof(MenuTitle));
        foreach (var option in Options)
        {
            var key = "shell.colorTheme." + option.Theme.ToString().ToLowerInvariant();
            option.Name = _localization.T(key);
            option.Hint = _localization.T(key + ".hint");
        }

        OnPropertyChanged(nameof(CurrentThemeName));
    }
}

public sealed partial class ColorThemeOption : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _hint = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public ColorThemeOption(PosColorTheme theme, IReadOnlyList<string> swatches)
    {
        Theme = theme;
        Swatches = swatches
            .Select(hex => (Brush)CreateFrozenBrush(hex))
            .ToArray();
    }

    public PosColorTheme Theme { get; }

    public IReadOnlyList<Brush> Swatches { get; }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
