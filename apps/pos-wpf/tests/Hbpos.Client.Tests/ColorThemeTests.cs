using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class ColorThemeTests(PaymentViewRuntimeStaTestHost host)
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] PaletteNames = ["Default", "Paper", "Graphite", "Celadon"];

    // 跟随主题的收银界面文件；客显与启动页保持独立配色，不在其中。
    private static readonly string[] ThemedXamlFiles =
    [
        "MainWindow.xaml",
        Path.Combine("Themes", "PosTheme.xaml"),
        Path.Combine("Views", "CardRecoveryCenterView.xaml"),
        Path.Combine("Views", "Controls", "ApiServerSettingsPanel.xaml"),
        Path.Combine("Views", "Screens", "DailyCloseView.xaml"),
        Path.Combine("Views", "Screens", "DeviceRegistrationView.xaml"),
        Path.Combine("Views", "Screens", "InstallmentCenterView.xaml"),
        Path.Combine("Views", "Screens", "InstallmentCreateView.xaml"),
        Path.Combine("Views", "Screens", "PaymentView.xaml"),
        Path.Combine("Views", "Screens", "PosTerminalView.xaml"),
        Path.Combine("Views", "Screens", "ReceiptReturnsView.xaml"),
        Path.Combine("Views", "Screens", "SettingsView.xaml"),
        Path.Combine("Views", "Screens", "SpecialProductsView.xaml"),
        Path.Combine("Views", "Screens", "TransactionHistoryView.xaml"),
        Path.Combine("Views", "Windows", "AppUpdatePromptWindow.xaml")
    ];

    [Theory]
    [InlineData(null, PosColorTheme.Default)]
    [InlineData("", PosColorTheme.Default)]
    [InlineData("Paper", PosColorTheme.Paper)]
    [InlineData("graphite", PosColorTheme.Graphite)]
    [InlineData("CELADON", PosColorTheme.Celadon)]
    [InlineData("Neon", PosColorTheme.Default)]
    [InlineData("99", PosColorTheme.Default)]
    public void Stored_theme_value_falls_back_to_default_when_unknown(string? stored, PosColorTheme expected)
    {
        Assert.Equal(expected, ColorThemeService.Parse(stored));
    }

    [Fact]
    public async Task Initialize_applies_the_saved_theme_before_the_window_shows()
    {
        var settings = new MemorySettings { [ColorThemeService.SettingKey] = "Graphite" };
        var applier = new RecordingApplier();
        var service = new ColorThemeService(settings, applier);

        await service.InitializeAsync();

        Assert.Equal(PosColorTheme.Graphite, service.Current);
        Assert.Equal([PosColorTheme.Graphite], applier.Applied);
    }

    [Fact]
    public async Task Initialize_keeps_default_theme_when_the_saved_preference_cannot_be_read()
    {
        var applier = new RecordingApplier();
        var service = new ColorThemeService(new MemorySettings { ThrowOnRead = true }, applier);

        await service.InitializeAsync();

        Assert.Equal(PosColorTheme.Default, service.Current);
        Assert.Equal([PosColorTheme.Default], applier.Applied);
    }

    [Fact]
    public async Task Select_applies_immediately_saves_the_preference_and_notifies_only_on_change()
    {
        var settings = new MemorySettings();
        var applier = new RecordingApplier();
        var service = new ColorThemeService(settings, applier);
        var changes = 0;
        service.ThemeChanged += (_, _) => changes++;

        await service.SelectAsync(PosColorTheme.Celadon);
        await service.SelectAsync(PosColorTheme.Celadon);

        Assert.Equal(PosColorTheme.Celadon, service.Current);
        Assert.Equal("Celadon", settings[ColorThemeService.SettingKey]);
        Assert.Equal([PosColorTheme.Celadon, PosColorTheme.Celadon], applier.Applied);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Palettes_define_the_same_colors_and_matching_metadata()
    {
        var palettes = PaletteNames.ToDictionary(name => name, LoadPalette);
        var defaultKeys = BrushKeys(palettes["Default"]);
        Assert.True(defaultKeys.Count >= 40, "默认色板应覆盖全部主题颜色。");

        foreach (var (name, palette) in palettes)
        {
            Assert.Equal(defaultKeys, BrushKeys(palette));
            Assert.Equal(name, ReadValue(palette, "PosPaletteId"));
            Assert.Equal(name == "Graphite" ? "True" : "False", ReadValue(palette, "PosPaletteIsDark"));
            // 默认主题不覆盖 MaterialDesign 主色，保证与改造前一致。
            Assert.Equal(name != "Default", ReadValue(palette, "PosPaletteMaterialPrimaryColor") is not null);
        }
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Paper")]
    [InlineData("Graphite")]
    [InlineData("Celadon")]
    public void Palette_text_and_filled_buttons_stay_readable(string name)
    {
        var colors = SolidColors(LoadPalette(name));
        var surface = colors["PosSurfaceBrush"];

        Assert.True(Contrast(colors["PosTextBrush"], surface) >= 7, "正文对比度应达到 AAA。");
        // 默认主题保持改造前的次要文字色（4.47:1），三套新主题都达到 AA。
        var mutedMinimum = name == "Default" ? 4.4 : 4.5;
        Assert.True(Contrast(colors["PosMutedForegroundBrush"], surface) >= mutedMinimum, "次要文字对比度不足。");
        Assert.True(Contrast(colors["PosPrimaryTextBrush"], surface) >= 4.5, "主色文字对比度应达到 AA。");
        // 各页面在主色、危险色填充上都用白字。
        Assert.True(Contrast(Colors.White, colors["PosPrimaryBrush"]) >= 4.5, "主色填充上的白字应达到 AA。");
        Assert.True(Contrast(Colors.White, colors["PosDangerBrush"]) >= 4.5, "危险色填充上的白字应达到 AA。");
    }

    [Fact]
    public void Themed_views_reference_palette_colors_dynamically_and_do_not_hard_code_colors()
    {
        var paletteKeys = BrushKeys(LoadPalette("Default"));
        var violations = new List<string>();
        var brushAttribute = new Regex(
            @"\b(Background|Foreground|BorderBrush|Fill|Stroke)=""(#(?:FF)?[0-9A-Fa-f]{6}|White|Black)""");
        var brushSetter = new Regex(
            @"<Setter\b[^>]*Property=""(?:Background|Foreground|BorderBrush|Fill|Stroke)""[^>]*Value=""(#(?:FF)?[0-9A-Fa-f]{6}|White|Black)""");
        var staticPaletteReference = new Regex(@"\{StaticResource (Pos[A-Za-z]+)\}");
        var dynamicPosReference = new Regex(@"\{DynamicResource (Pos[A-Za-z]+Brush)\}");

        foreach (var relative in ThemedXamlFiles)
        {
            var text = File.ReadAllText(Path.Combine(WpfRoot(), relative));
            foreach (Match match in brushAttribute.Matches(text))
            {
                // 不透明度遮罩里的颜色只决定形状，不会显示出来。
                if (text.LastIndexOf("OpacityMask>", match.Index, StringComparison.Ordinal) >
                    text.LastIndexOf("</Border.OpacityMask>", match.Index, StringComparison.Ordinal))
                {
                    continue;
                }

                // 白字只出现在主色、状态色等填充上，四套主题的填充都配白字。
                if (match.Groups[1].Value == "Foreground" && match.Groups[2].Value == "White")
                {
                    continue;
                }

                // 现金面额黄底上的深棕字，属于钞票配色，不随主题变化。
                if (match.Groups[2].Value.Equals("#FF2E1500", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                violations.Add($"{relative}: {match.Value}");
            }

            foreach (Match match in brushSetter.Matches(text))
            {
                if (match.Value.Contains("Property=\"Foreground\"", StringComparison.Ordinal) &&
                    (match.Groups[1].Value == "White" || match.Groups[1].Value.Equals("#FF2E1500", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                violations.Add($"{relative}: {match.Value}");
            }

            violations.AddRange(staticPaletteReference.Matches(text)
                .Where(match => paletteKeys.Contains(match.Groups[1].Value))
                .Select(match => $"{relative}: {match.Value} 应改为 DynamicResource"));
            violations.AddRange(dynamicPosReference.Matches(text)
                .Where(match => !paletteKeys.Contains(match.Groups[1].Value))
                .Select(match => $"{relative}: {match.Value} 在色板中不存在"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public Task Wpf_applier_swaps_the_palette_and_dynamic_references_follow()
    {
        return host.RunAsync(application =>
        {
            var applier = new WpfColorThemeApplier();
            var border = new Border();
            border.SetResourceReference(Border.BackgroundProperty, "PosSurfaceBrush");
            // 应用级资源变化只通知窗口里的元素；真实界面的控件都在窗口中。
            var window = new Window { Content = border };
            try
            {
                applier.Apply(PosColorTheme.Graphite);

                Assert.Equal("Graphite", application.Resources["PosPaletteId"]);
                Assert.Single(application.Resources.MergedDictionaries, dictionary => dictionary.Contains("PosPaletteId"));
                Assert.Equal(Color.FromRgb(0x1C, 0x20, 0x25), Assert.IsType<SolidColorBrush>(border.Background).Color);
            }
            finally
            {
                // 共享测试 Application 的其他用例依赖默认色板。
                applier.Apply(PosColorTheme.Default);
            }

            Assert.Equal("Default", application.Resources["PosPaletteId"]);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(border.Background).Color);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Switcher_lists_four_themes_marks_the_current_one_and_closes_after_selection()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var service = new ColorThemeService(new MemorySettings(), new RecordingApplier());
        var switcher = new ColorThemeSwitcherViewModel(service, localization);

        Assert.Equal("界面配色", switcher.MenuTitle);
        Assert.Equal(["默认", "暖纸", "石墨夜", "青瓷"], switcher.Options.Select(option => option.Name));
        Assert.All(switcher.Options, option => Assert.Equal(3, option.Swatches.Count));
        Assert.True(switcher.Options.Single(option => option.Theme == PosColorTheme.Default).IsSelected);

        switcher.IsMenuOpen = true;
        await switcher.SelectCommand.ExecuteAsync(PosColorTheme.Graphite);

        Assert.False(switcher.IsMenuOpen);
        Assert.Equal(PosColorTheme.Graphite, service.Current);
        Assert.Equal([PosColorTheme.Graphite], switcher.Options.Where(option => option.IsSelected).Select(option => option.Theme));

        localization.SetCulture("en-US");
        Assert.Equal("Color theme", switcher.MenuTitle);
        Assert.Equal(["Default", "Paper", "Graphite", "Celadon"], switcher.Options.Select(option => option.Name));
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Paper")]
    [InlineData("Graphite")]
    [InlineData("Celadon")]
    public void Sidebar_icons_keep_function_colors_in_default_and_share_one_ink_color_in_new_themes(string name)
    {
        var colors = SolidColors(LoadPalette(name));
        var icon = colors["PosSidebarIconBrush"];
        var refund = colors["PosSidebarRefundIconBrush"];
        var hold = colors["PosSidebarHoldIconBrush"];

        if (name == "Default")
        {
            // 默认主题与改造前一致：功能图标主色、退款红色、挂单橙色。
            Assert.Equal(colors["PosPrimaryTextBrush"], icon);
            Assert.Equal(colors["PosDangerTextBrush"], refund);
            Assert.Equal(colors["PosAccentBrush"], hold);
        }
        else
        {
            Assert.Equal(icon, refund);
            Assert.Equal(icon, hold);
            Assert.True(Contrast(icon, colors["PosSurfaceBrush"]) >= 4.5, "侧栏图标对比度应达到 AA。");
        }
    }

    [Fact]
    public void Pos_sidebar_icons_use_sidebar_colors_and_exit_stays_danger()
    {
        var view = XDocument.Load(Path.Combine(WpfRoot(), "Views", "Screens", "PosTerminalView.xaml"));
        XNamespace materialDesign = "http://materialdesigninxaml.net/winfx/xaml/themes";
        var sidebarButtons = view.Descendants(Presentation + "Button")
            .Where(button => ((string?)button.Attribute("Style"))?.Contains("PosSidebar", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(10, sidebarButtons.Length);

        var iconColors = sidebarButtons.ToDictionary(
            button => (string)button.Attribute("Command")!,
            button => (string?)button.Descendants(materialDesign + "PackIcon").Single().Attribute("Foreground"));

        Assert.Equal("{DynamicResource PosSidebarRefundIconBrush}", iconColors["{Binding OpenReturnsCommand}"]);
        Assert.Equal("{DynamicResource PosSidebarHoldIconBrush}", iconColors["{Binding HoldOrderCommand}"]);
        Assert.Equal("{DynamicResource PosDangerTextBrush}", iconColors["{Binding ExitApplicationCommand}"]);
        Assert.All(
            iconColors.Where(pair => pair.Key is not ("{Binding OpenReturnsCommand}" or "{Binding HoldOrderCommand}" or "{Binding ExitApplicationCommand}")),
            pair => Assert.Equal("{DynamicResource PosSidebarIconBrush}", pair.Value));
    }

    [Fact]
    public void Title_bar_places_the_color_theme_switcher_next_to_the_customer_display_button()
    {
        var window = XDocument.Load(Path.Combine(WpfRoot(), "MainWindow.xaml"));
        var customerDisplayButton = Assert.Single(window.Descendants(Presentation + "Button").Where(button =>
            (string?)button.Attribute("Command") == "{Binding ToggleCustomerDisplayWindowCommand}"));
        var switcher = Assert.IsType<XElement>(customerDisplayButton.ElementsAfterSelf().First());

        Assert.Equal("ColorThemeSwitcher", (string?)switcher.Attribute(Xaml + "Name"));
        var toggle = Assert.Single(switcher.Elements(Presentation + "ToggleButton"));
        Assert.Equal("ColorThemeToggle", (string?)toggle.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("{Binding IsMenuOpen, Mode=TwoWay}", (string?)toggle.Attribute("IsChecked"));
        var popup = Assert.Single(switcher.Elements(Presentation + "Popup"));
        Assert.Equal("False", (string?)popup.Attribute("StaysOpen"));
    }

    [Fact]
    public void Window_foreground_and_plain_text_inputs_follow_the_palette_text_color()
    {
        // 真机巡检：石墨夜下未着色图标、系统模板输入框都落回黑色，压在深色底上看不见。
        var window = XDocument.Load(Path.Combine(WpfRoot(), "MainWindow.xaml"));
        Assert.Equal("{DynamicResource PosTextBrush}", (string?)window.Root!.Attribute("Foreground"));

        var theme = XDocument.Load(Path.Combine(WpfRoot(), "Themes", "PosTheme.xaml"));
        var settings = XDocument.Load(Path.Combine(WpfRoot(), "Views", "Screens", "SettingsView.xaml"));
        foreach (var (document, key) in new[]
                 {
                     (theme, "PosSearchTextBoxStyle"),
                     (settings, "SettingsFieldPasswordBoxStyle")
                 })
        {
            var setters = StyleSetters(document, key);
            Assert.Equal("{DynamicResource PosTextBrush}", setters["Foreground"]);
            Assert.Equal("{DynamicResource PosTextBrush}", setters["CaretBrush"]);
        }

        var comboStyle = settings.Descendants(Presentation + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == "SettingsComboBoxStyle");
        Assert.Equal("{StaticResource {x:Type ComboBox}}", (string?)comboStyle.Attribute("BasedOn"));
        Assert.Equal("{DynamicResource PosTextBrush}", StyleSetters(settings, "SettingsComboBoxStyle")["Foreground"]);
    }

    private static Dictionary<string, string> StyleSetters(XDocument document, string key) =>
        document.Descendants(Presentation + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == key)
            .Elements(Presentation + "Setter")
            .ToDictionary(setter => (string)setter.Attribute("Property")!, setter => (string)setter.Attribute("Value")!);

    private static XDocument LoadPalette(string name) =>
        XDocument.Load(Path.Combine(WpfRoot(), "Themes", "Palettes", name + ".xaml"));

    private static HashSet<string> BrushKeys(XDocument palette) =>
        palette.Root!.Elements()
            .Where(element => element.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush")
            .Select(element => (string)element.Attribute(Xaml + "Key")!)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, Color> SolidColors(XDocument palette) =>
        palette.Root!.Elements(Presentation + "SolidColorBrush").ToDictionary(
            element => (string)element.Attribute(Xaml + "Key")!,
            element => (Color)ColorConverter.ConvertFromString((string)element.Attribute("Color")!)!);

    private static string? ReadValue(XDocument palette, string key) =>
        palette.Root!.Elements().SingleOrDefault(element => (string?)element.Attribute(Xaml + "Key") == key)?.Value;

    private static double Contrast(Color foreground, Color background)
    {
        static double Channel(byte value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static string WpfRoot() =>
        Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "hb-platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }

    private sealed class RecordingApplier : IColorThemeApplier
    {
        public List<PosColorTheme> Applied { get; } = [];

        public void Apply(PosColorTheme theme) => Applied.Add(theme);
    }

    private sealed class MemorySettings : Dictionary<string, string>, ILocalAppSettingsRepository
    {
        public bool ThrowOnRead { get; init; }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("settings unavailable");
            }

            return Task.FromResult(TryGetValue(key, out var value) ? value : null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            this[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
            {
                this[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
