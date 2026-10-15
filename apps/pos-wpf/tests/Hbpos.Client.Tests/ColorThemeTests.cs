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
    private static readonly string[] PaletteNames = ["Default", "Paper", "Graphite", "Celadon", "Blue", "Elder"];

    // 跟随主题的收银界面文件；客显与启动页保持独立配色，不在其中。
    private static readonly string[] ThemedXamlFiles =
    [
        "MainWindow.xaml",
        Path.Combine("Themes", "PosTheme.xaml"),
        Path.Combine("Views", "CardRecoveryCenterView.xaml"),
        Path.Combine("Views", "Controls", "ApiServerSettingsPanel.xaml"),
        Path.Combine("Views", "Controls", "PosDatePicker.xaml"),
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
    [InlineData(null, PosColorTheme.Blue)]
    [InlineData("", PosColorTheme.Blue)]
    [InlineData("Default", PosColorTheme.Default)]
    [InlineData("blue", PosColorTheme.Blue)]
    [InlineData("Paper", PosColorTheme.Paper)]
    [InlineData("graphite", PosColorTheme.Graphite)]
    [InlineData("CELADON", PosColorTheme.Celadon)]
    [InlineData("elder", PosColorTheme.Elder)]
    [InlineData("Neon", PosColorTheme.Blue)]
    [InlineData("99", PosColorTheme.Blue)]
    public void Stored_theme_value_falls_back_to_blue_when_missing_or_unknown(string? stored, PosColorTheme expected)
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
    public async Task Initialize_uses_the_blue_theme_when_the_saved_preference_cannot_be_read()
    {
        var applier = new RecordingApplier();
        var service = new ColorThemeService(new MemorySettings { ThrowOnRead = true }, applier);

        await service.InitializeAsync();

        Assert.Equal(PosColorTheme.Blue, service.Current);
        Assert.Equal([PosColorTheme.Blue], applier.Applied);
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
    [InlineData("Blue")]
    [InlineData("Elder")]
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
    public async Task Switcher_lists_six_themes_with_blue_first_marks_the_current_one_and_closes_after_selection()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var service = new ColorThemeService(new MemorySettings(), new RecordingApplier());
        var switcher = new ColorThemeSwitcherViewModel(service, localization);

        Assert.Equal("界面配色", switcher.MenuTitle);
        Assert.Equal(["蓝色", "经典", "暖纸", "石墨夜", "青瓷", "长者模式"], switcher.Options.Select(option => option.Name));
        Assert.All(switcher.Options, option => Assert.Equal(3, option.Swatches.Count));
        // 没有保存过偏好的收银机默认是蓝色。
        Assert.True(switcher.Options.Single(option => option.Theme == PosColorTheme.Blue).IsSelected);

        switcher.IsMenuOpen = true;
        await switcher.SelectCommand.ExecuteAsync(PosColorTheme.Graphite);

        Assert.False(switcher.IsMenuOpen);
        Assert.Equal(PosColorTheme.Graphite, service.Current);
        Assert.Equal([PosColorTheme.Graphite], switcher.Options.Where(option => option.IsSelected).Select(option => option.Theme));

        localization.SetCulture("en-US");
        Assert.Equal("Color theme", switcher.MenuTitle);
        Assert.Equal(["Blue", "Classic", "Paper", "Graphite", "Celadon", "Easy View"], switcher.Options.Select(option => option.Name));
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Paper")]
    [InlineData("Graphite")]
    [InlineData("Celadon")]
    [InlineData("Blue")]
    [InlineData("Elder")]
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
            // 长者主题的功能按钮是深蓝实心底配白图标，图标坐在按钮底色上而不是白卡片上。
            var iconBackground = name == "Elder" ? colors["PosSidebarButtonBrush"] : colors["PosSurfaceBrush"];
            Assert.True(Contrast(icon, iconBackground) >= 4.5, "侧栏图标对比度应达到 AA。");
        }
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Paper")]
    [InlineData("Graphite")]
    [InlineData("Celadon")]
    [InlineData("Blue")]
    public void Legacy_palettes_map_the_new_semantic_keys_to_the_pre_redesign_colors(string name)
    {
        // 长者模式新增的语义键在旧主题里必须等于改版前实际显示的颜色，保证旧主题视觉不变。
        var colors = SolidColors(LoadPalette(name));
        var equivalents = new (string Key, string Legacy)[]
        {
            ("PosZoneCartBrush", "PosSurfaceAltBrush"),
            ("PosZoneKeypadBrush", "PosSurfaceAltBrush"),
            ("PosZoneFuncBrush", "PosSurfaceAltBrush"),
            ("PosZoneDividerBrush", "PosDividerBrush"),
            ("PosKeyBorderBrush", "PosBorderBrush"),
            ("PosCardBorderBrush", "PosDividerBrush"),
            ("PosCartHeaderBrush", "PosSurfaceAltBrush"),
            ("PosCartAltRowBrush", "PosSurfaceAltBrush"),
            ("PosCartSelectedRowBrush", "PosPrimarySoftBrush"),
            ("PosDueAmountBrush", "PosAccentBrush"),
            ("PosClearKeyBrush", "PosSurfaceBrush"),
            ("PosNoBarcodeBrush", "PosPrimaryBrush"),
            ("PosNoBarcodePressedBrush", "PosPrimaryStrongBrush"),
            ("PosNoBarcodeDisabledBrush", "PosPrimaryDisabledBrush"),
            ("PosSidebarButtonBrush", "PosSurfaceBrush"),
            ("PosSidebarButtonForegroundBrush", "PosTextBrush"),
            ("PosSidebarButtonHoverBrush", "PosSurfaceAltBrush"),
            ("PosSidebarButtonPressedBrush", "PosPrimarySoftBrush"),
            ("PosSidebarExitButtonBrush", "PosSurfaceBrush"),
            ("PosSidebarExitForegroundBrush", "PosDangerTextBrush"),
            ("PosSidebarExitHoverBrush", "PosSurfaceAltBrush"),
            // 退出按钮按下：旧主题沿用基础键模板的按下色（PosPrimarySoftBrush），不是危险浅红。
            ("PosSidebarExitPressedBrush", "PosPrimarySoftBrush"),
            ("PosConfirmPaymentBrush", "PosPrimaryBrush"),
            ("PosCancelButtonBrush", "PosSurfaceBrush"),
            ("PosCancelButtonForegroundBrush", "PosPrimaryDarkBrush"),
            ("PosAmountEmphasisBrush", "PosPrimaryTextBrush"),
            ("PosChangeDueBackgroundBrush", "PosSuccessSoftBrush"),
            ("PosChangeDueForegroundBrush", "PosSuccessTextBrush"),
            ("PosQtyPlusBrush", "PosSurfaceBrush"),
            ("PosQtyPlusForegroundBrush", "PosSuccessTextBrush"),
            ("PosDiscountKeyForegroundBrush", "PosTextBrush"),
            ("PosSegmentSeparatorBrush", "PosBorderBrush"),
            ("PosPopupBorderBrush", "PosDividerBrush")
        };

        foreach (var (key, legacy) in equivalents)
        {
            Assert.True(colors[key] == colors[legacy], $"{name}: {key} 应等于改版前的 {legacy}");
        }

        Assert.Equal(Colors.White, colors["PosNoBarcodeForegroundBrush"]);
        // 投影键在旧主题里完全透明（不画任何厚度）。
        Assert.Equal(0, colors["PosKeyShadowBrush"].A);
        Assert.Equal(0, colors["PosConfirmPaymentShadowBrush"].A);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Paper")]
    [InlineData("Graphite")]
    [InlineData("Celadon")]
    [InlineData("Blue")]
    [InlineData("Elder")]
    public void Payment_method_and_note_colors_are_readable_in_every_palette(string name)
    {
        var colors = SolidColors(LoadPalette(name));
        var surface = colors["PosSurfaceBrush"];
        var failures = new List<string>();
        foreach (var method in new[] { "Cash", "Card", "Voucher" })
        {
            var fill = colors[$"PosMethod{method}FillBrush"];
            var on = colors[$"PosMethod{method}OnBrush"];
            var soft = colors[$"PosMethod{method}SoftBrush"];
            var ink = colors[$"PosMethod{method}InkBrush"];
            if (Contrast(on, fill) < 4.5) failures.Add($"{method} 选中实心上的字 {Contrast(on, fill):F2}");
            if (Contrast(ink, soft) < 4.5) failures.Add($"{method} 浅色块上的字 {Contrast(ink, soft):F2}");
            if (Contrast(fill, surface) < 3) failures.Add($"{method} 实心块对卡片底色 {Contrast(fill, surface):F2}");
        }

        foreach (var amount in new[] { 100, 50, 20, 10, 5 })
        {
            var fill = colors[$"PosNote{amount}FillBrush"];
            var text = colors[$"PosNote{amount}TextBrush"];
            if (Contrast(text, fill) < 4.5) failures.Add($"${amount} 面额数字 {Contrast(text, fill):F2}");
        }

        Assert.True(failures.Count == 0, $"{name}: " + string.Join("; ", failures));
    }

    [Fact]
    public void Elder_palette_keeps_text_and_action_buttons_at_high_contrast()
    {
        var colors = SolidColors(LoadPalette("Elder"));
        var failures = new List<string>();
        void Require(string label, string foreground, string background, double minimum)
        {
            var ratio = Contrast(colors[foreground], colors[background]);
            if (ratio < minimum) failures.Add($"{label} {ratio:F2} < {minimum}");
        }

        Require("功能按钮白字", "PosSidebarButtonForegroundBrush", "PosSidebarButtonBrush", 7);
        Require("退出按钮白字", "PosSidebarExitForegroundBrush", "PosSidebarExitButtonBrush", 4.5);
        Require("无条码按钮文字", "PosNoBarcodeForegroundBrush", "PosNoBarcodeBrush", 7);
        Require("取消按钮文字", "PosCancelButtonForegroundBrush", "PosCancelButtonBrush", 7);
        Require("Clear 键文字", "PosDangerTextBrush", "PosClearKeyBrush", 7);
        Require("找零文字", "PosChangeDueForegroundBrush", "PosChangeDueBackgroundBrush", 7);
        Require("待付金额", "PosAmountEmphasisBrush", "PosSurfaceBrush", 7);
        Require("应付合计", "PosDueAmountBrush", "PosSurfaceBrush", 7);
        Require("购物车正文", "PosTextBrush", "PosZoneCartBrush", 7);
        Require("键盘区正文", "PosTextBrush", "PosZoneKeypadBrush", 7);
        Require("功能区正文", "PosTextBrush", "PosZoneFuncBrush", 7);
        Require("选中行正文", "PosTextBrush", "PosCartSelectedRowBrush", 7);
        // 白字压在确认支付的纯绿上（5.0+）和 Pay Now 渐变上。
        Assert.True(Contrast(Colors.White, colors["PosConfirmPaymentBrush"]) >= 7, "确认支付白字应达到 AAA。");
        // 长者主题的三个区必须是三种不同的底色。
        Assert.Equal(3, new[] { colors["PosZoneCartBrush"], colors["PosZoneKeypadBrush"], colors["PosZoneFuncBrush"] }.Distinct().Count());
        // 内容区零描边：键、卡片描边与分区线透明。
        Assert.Equal(0, colors["PosKeyBorderBrush"].A);
        Assert.Equal(0, colors["PosCardBorderBrush"].A);
        Assert.Equal(0, colors["PosZoneDividerBrush"].A);
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public void Elder_metrics_only_override_keys_that_the_standard_metrics_define()
    {
        var themeText = File.ReadAllText(Path.Combine(WpfRoot(), "Themes", "PosTheme.xaml"));
        foreach (var area in new[] { "Home", "Payment" })
        {
            var standard = MetricKeys(area + ".Standard");
            var elder = MetricKeys(area + ".Elder");
            Assert.Contains("PosMetricsElderId", elder);
            Assert.DoesNotContain("PosMetricsElderId", standard);
            elder.Remove("PosMetricsElderId");
            Assert.True(elder.IsSubsetOf(standard), $"{area}.Elder 里这些键在标准字典里不存在：{string.Join(", ", elder.Except(standard))}");
            // 标准字典必须由 PosTheme.xaml 始终合并，旧主题与测试宿主才有这些值。
            Assert.Contains($"Metrics/{area}.Standard.xaml", themeText);
        }
    }

    [Fact]
    public void Elder_sidebar_labels_have_room_for_two_natural_text_lines()
    {
        // WPF 的 TextBlock 默认按字体自然行高（≈ 字号×1.33）叠行，LineHeight 并不生效：
        // MaxHeight 小于两行的自然高度时，第二行会被截掉，英文「Special Items」「Hold Order」只剩首词加省略号（真机截图发现）。
        // 长者主题字号更大，必须按它自己的字号核对；标准值保持改版前的数字，不在此约束。
        var elder = XDocument.Load(Path.Combine(WpfRoot(), "Themes", "Metrics", "Home.Elder.xaml"));
        double Value(string key) => double.Parse(
            elder.Descendants().Single(element => (string?)element.Attribute(Xaml + "Key") == key).Value,
            CultureInfo.InvariantCulture);

        Assert.True(
            Value("PosHomeSidebarLabelMaxHeight") >= 2 * Value("PosHomeSidebarLabelFontSize") * 1.34,
            "长者主题功能区标签的 MaxHeight 装不下两行文字。");
    }

    [Fact]
    public void Non_brush_theme_resources_referenced_by_views_exist_in_the_standard_dictionaries()
    {
        // 视图里的尺寸资源（字号、行高…）必须有标准值，否则旧主题下 DynamicResource 找不到会退回 WPF 默认值。
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(WpfRoot(), "Themes", "Metrics"), "*.Standard.xaml"))
        {
            defined.UnionWith(ElementKeys(XDocument.Load(file)));
        }

        defined.UnionWith(ElementKeys(XDocument.Load(Path.Combine(WpfRoot(), "Themes", "PosTheme.xaml"))));
        var reference = new Regex(@"\{DynamicResource (Pos[A-Za-z0-9]+)\}");
        var missing = new List<string>();
        foreach (var relative in ThemedXamlFiles)
        {
            var text = File.ReadAllText(Path.Combine(WpfRoot(), relative));
            missing.AddRange(reference.Matches(text)
                .Select(match => match.Groups[1].Value)
                .Where(key => !key.EndsWith("Brush", StringComparison.Ordinal) && !defined.Contains(key))
                .Distinct()
                .Select(key => $"{relative}: {key}"));
        }

        Assert.Empty(missing);
    }

    [Fact]
    public Task Wpf_applier_appends_elder_metrics_and_other_themes_remove_them()
    {
        return host.RunAsync(application =>
        {
            var applier = new WpfColorThemeApplier();
            try
            {
                applier.Apply(PosColorTheme.Elder);
                var merged = application.Resources.MergedDictionaries;
                Assert.Equal("Elder", application.Resources["PosPaletteId"]);
                var metricIndexes = merged.Select((dictionary, index) => (dictionary, index))
                    .Where(pair => pair.dictionary.Contains(WpfColorThemeApplier.ElderMetricsIdKey))
                    .Select(pair => pair.index)
                    .ToArray();
                Assert.Equal(2, metricIndexes.Length);
                // 放大尺寸必须排在 PosTheme（含标准尺寸）之后，才能覆盖同名键。
                var themeIndex = merged
                    .Select((dictionary, index) => (dictionary, index))
                    .Last(pair => pair.dictionary.Contains("PosFontFamily")).index;
                Assert.All(metricIndexes, index => Assert.True(index > themeIndex));

                // 再切到别的主题：放大字典整体移除，标准尺寸生效。
                applier.Apply(PosColorTheme.Blue);
                Assert.DoesNotContain(application.Resources.MergedDictionaries, dictionary => dictionary.Contains(WpfColorThemeApplier.ElderMetricsIdKey));
                Assert.Single(application.Resources.MergedDictionaries, dictionary => dictionary.Contains("PosPaletteId"));
            }
            finally
            {
                applier.Apply(PosColorTheme.Default);
            }

            return Task.CompletedTask;
        });
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
        // 退出图标改走 PosSidebarExitForegroundBrush：旧主题取值＝PosDangerTextBrush（仍是危险红），长者主题是红底上的白色。
        Assert.Equal("{DynamicResource PosSidebarExitForegroundBrush}", iconColors["{Binding ExitApplicationCommand}"]);
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

    private static HashSet<string> ElementKeys(XDocument document) =>
        document.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> MetricKeys(string name) =>
        ElementKeys(XDocument.Load(Path.Combine(WpfRoot(), "Themes", "Metrics", name + ".xaml")));

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
