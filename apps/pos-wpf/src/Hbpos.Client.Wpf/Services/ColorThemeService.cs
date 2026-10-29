using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace Hbpos.Client.Wpf.Services;

/// <summary>收银主界面的配色主题；客显保持独立配色，不随主题切换。</summary>
public enum PosColorTheme
{
    Graphite,
    Celadon,
    Blue,
    // 长者模式：高对比、大字、首页三区分色、内容区无描边；除色板外还会追加放大尺寸字典（见 WpfColorThemeApplier）。
    Elder,
    // 四套彩色主题：糖果紫 / 阳光橙只换色板（功能键白底彩色图标）；海洋青 / 彩虹分区的功能键每键一种底色（PosSidebarTile* 键）。
    Candy,
    Sunny,
    Ocean,
    Rainbow
}

public interface IColorThemeService
{
    PosColorTheme Current { get; }

    event EventHandler? ThemeChanged;

    /// <summary>读取本机保存的主题并应用；读取失败时保持默认主题。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>立即切换主题并保存为本机偏好。</summary>
    Task SelectAsync(PosColorTheme theme, CancellationToken cancellationToken = default);
}

/// <summary>把主题落到界面资源上；与偏好读写分开，便于在没有 WPF 资源的环境下测试服务。</summary>
public interface IColorThemeApplier
{
    void Apply(PosColorTheme theme);
}

public sealed class ColorThemeService : IColorThemeService
{
    internal const string SettingKey = "Shell:ColorTheme";

    /// <summary>没有保存过偏好（或偏好无法读取）时使用的配色；已保存的选择不受影响。</summary>
    public const PosColorTheme FallbackTheme = PosColorTheme.Blue;

    private readonly ILocalAppSettingsRepository _settings;
    private readonly IColorThemeApplier _applier;

    public ColorThemeService(ILocalAppSettingsRepository settings, IColorThemeApplier? applier = null)
    {
        _settings = settings;
        _applier = applier ?? new WpfColorThemeApplier();
    }

    public PosColorTheme Current { get; private set; } = FallbackTheme;

    public event EventHandler? ThemeChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var theme = FallbackTheme;
        try
        {
            theme = Parse(await _settings.GetValueAsync(SettingKey, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 配色偏好损坏不应阻断收银启动。
            ConsoleLog.WriteError("ColorTheme", $"load failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
        }

        Apply(theme);
    }

    public async Task SelectAsync(PosColorTheme theme, CancellationToken cancellationToken = default)
    {
        Apply(theme);
        try
        {
            await _settings.SetValueAsync(SettingKey, theme.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteError("ColorTheme", $"save failed theme={theme} error={ex.GetType().Name} message={ex.Message}", exception: ex);
        }
    }

    internal static PosColorTheme Parse(string? value)
    {
        // 已删除的旧主题：「经典」（Default）改由彩色的糖果紫接替，「暖纸」（Paper）回到默认蓝色。
        if (string.Equals(value, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return PosColorTheme.Candy;
        }

        return Enum.TryParse<PosColorTheme>(value, ignoreCase: true, out var theme) && Enum.IsDefined(theme)
            ? theme
            : FallbackTheme;
    }

    private void Apply(PosColorTheme theme)
    {
        _applier.Apply(theme);
        var changed = Current != theme;
        Current = theme;
        ConsoleLog.Write("ColorTheme", $"applied theme={theme}");
        if (changed)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>替换 App 资源中的色板字典，并同步 MaterialDesign 的深浅底色与主色。</summary>
public sealed class WpfColorThemeApplier : IColorThemeApplier
{
    internal const string PaletteIdKey = "PosPaletteId";

    // 长者模式的尺寸字典都带这个标记键，用来在切换主题时找到并整体移除。
    internal const string ElderMetricsIdKey = "PosMetricsElderId";

    // 标准尺寸写在 PosTheme.xaml 合并的 Metrics/*.Standard.xaml（始终加载，与改版前字面量一致）；
    // 长者模式把下面这些字典追加到应用资源末尾，同名键后加载者生效，因此只需覆盖要变的尺寸。
    private static readonly string[] ElderMetricsDictionaries = ["Home", "Payment"];
    private const string PaletteIsDarkKey = "PosPaletteIsDark";
    private const string PaletteMaterialPrimaryKey = "PosPaletteMaterialPrimaryColor";

    private Theme? _originalMaterialTheme;

    public void Apply(PosColorTheme theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var palette = new ResourceDictionary { Source = GetPaletteUri(theme) };
        ReplacePalette(application.Resources, palette);
        ReplaceElderMetrics(application.Resources, theme == PosColorTheme.Elder);
        ApplyMaterialTheme(palette);
    }

    internal static Uri GetPaletteUri(PosColorTheme theme)
    {
        return new Uri(
            $"pack://application:,,,/Hbpos.Client.Wpf;component/Themes/Palettes/{theme}.xaml",
            UriKind.Absolute);
    }

    internal static Uri GetElderMetricsUri(string area)
    {
        return new Uri(
            $"pack://application:,,,/Hbpos.Client.Wpf;component/Themes/Metrics/{area}.Elder.xaml",
            UriKind.Absolute);
    }

    /// <summary>
    /// 长者模式：先移除旧的放大尺寸字典，再按需追加到合并字典末尾（覆盖 PosTheme 里的标准尺寸）；
    /// 其它主题只移除，尺寸回到标准值。DynamicResource 引用随字典增删自动刷新。
    /// </summary>
    internal static void ReplaceElderMetrics(ResourceDictionary resources, bool enable)
    {
        var merged = resources.MergedDictionaries;
        for (var index = merged.Count - 1; index >= 0; index--)
        {
            if (merged[index].Contains(ElderMetricsIdKey))
            {
                merged.RemoveAt(index);
            }
        }

        if (!enable)
        {
            return;
        }

        foreach (var area in ElderMetricsDictionaries)
        {
            merged.Add(new ResourceDictionary { Source = GetElderMetricsUri(area) });
        }
    }

    /// <summary>用新色板替换资源中的旧色板；所有 DynamicResource 引用随之刷新。</summary>
    internal static void ReplacePalette(ResourceDictionary resources, ResourceDictionary palette)
    {
        var merged = resources.MergedDictionaries;
        for (var index = 0; index < merged.Count; index++)
        {
            if (merged[index].Contains(PaletteIdKey))
            {
                merged[index] = palette;
                return;
            }
        }

        // 没有旧色板时放在最前，保证后续字典里的 DynamicResource 都能解析到。
        merged.Insert(0, palette);
    }

    private void ApplyMaterialTheme(ResourceDictionary palette)
    {
        try
        {
            var paletteHelper = new PaletteHelper();
            _originalMaterialTheme ??= paletteHelper.GetTheme();
            if (palette[PaletteMaterialPrimaryKey] is not Color primary)
            {
                // 色板未提供主色时恢复 MaterialDesign 原配置（现有色板都提供主色，这里只是兜底）。
                paletteHelper.SetTheme(_originalMaterialTheme);
                return;
            }

            var theme = paletteHelper.GetTheme();
            theme.SetBaseTheme(palette[PaletteIsDarkKey] is true ? BaseTheme.Dark : BaseTheme.Light);
            theme.SetPrimaryColor(primary);
            paletteHelper.SetTheme(theme);
        }
        catch (Exception ex)
        {
            // MaterialDesign 主题未加载（如测试宿主）时只切换 POS 色板。
            ConsoleLog.WriteError("ColorTheme", $"material theme apply failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
        }
    }
}
