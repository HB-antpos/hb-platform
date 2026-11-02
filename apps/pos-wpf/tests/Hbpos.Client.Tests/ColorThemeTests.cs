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
    private static readonly string[] PaletteNames = ["Blue", "Graphite", "Celadon", "Elder", "Candy", "Sunny", "Ocean", "Rainbow"];

    // 功能键每键一种底色的彩色主题；其余主题的逐键底色都等于功能键底色。
    private static readonly string[] MulticolorSidebarPalettes = ["Ocean", "Rainbow"];

    private static readonly string[] SidebarTileKeys =
    [
        "PosSidebarTileSpecialBrush", "PosSidebarTileRefundBrush", "PosSidebarTileHoldBrush", "PosSidebarTileRecallBrush",
        "PosSidebarTileDrawerBrush", "PosSidebarTileReceiptBrush", "PosSidebarTileLockBrush", "PosSidebarTileDailyCloseBrush",
        "PosSidebarTileCardRecoveryBrush", "PosSidebarTileAttendanceBrush", "PosSidebarTileSettingsBrush"
    ];

    // 跟随主题的收银界面文件；客显与启动页保持独立配色，不在其中。
    private static readonly string[] ThemedXamlFiles =
    [
        "MainWindow.xaml",
        Path.Combine("Themes", "PosTheme.xaml"),
        Path.Combine("Views", "CardRecoveryCenterView.xaml"),
        Path.Combine("Views", "Controls", "ApiServerSettingsPanel.xaml"),
        Path.Combine("Views", "Controls", "ColorThemeOnboardingOverlay.xaml"),
        Path.Combine("Views", "Controls", "NetworkDiagnosticsPanel.xaml"),
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
    [InlineData("blue", PosColorTheme.Blue)]
    [InlineData("graphite", PosColorTheme.Graphite)]
    [InlineData("CELADON", PosColorTheme.Celadon)]
    [InlineData("elder", PosColorTheme.Elder)]
    [InlineData("Candy", PosColorTheme.Candy)]
    [InlineData("sunny", PosColorTheme.Sunny)]
    [InlineData("Ocean", PosColorTheme.Ocean)]
    [InlineData("RAINBOW", PosColorTheme.Rainbow)]
    // 已删除的旧主题：经典由糖果紫接替，暖纸回到默认蓝色。
    [InlineData("Default", PosColorTheme.Candy)]
    [InlineData("default", PosColorTheme.Candy)]
    [InlineData("Paper", PosColorTheme.Blue)]
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
        var defaultKeys = BrushKeys(palettes["Blue"]);
        Assert.True(defaultKeys.Count >= 40, "默认色板应覆盖全部主题颜色。");
        // 色板目录里只能有登记过的主题（删除的经典 / 暖纸不能残留）。
        Assert.Equal(
            PaletteNames.Order(StringComparer.Ordinal),
            Directory.GetFiles(Path.Combine(WpfRoot(), "Themes", "Palettes"), "*.xaml").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));
        Assert.Equal(PaletteNames.Order(StringComparer.Ordinal), Enum.GetNames<PosColorTheme>().Order(StringComparer.Ordinal));

        foreach (var (name, palette) in palettes)
        {
            Assert.Equal(defaultKeys, BrushKeys(palette));
            Assert.Equal(name, ReadValue(palette, "PosPaletteId"));
            Assert.Equal(name == "Graphite" ? "True" : "False", ReadValue(palette, "PosPaletteIsDark"));
            Assert.NotNull(ReadValue(palette, "PosPaletteMaterialPrimaryColor"));
        }
    }

    [Theory]
    [MemberData(nameof(AllPalettes))]
    public void Palette_text_and_filled_buttons_stay_readable(string name)
    {
        var colors = SolidColors(LoadPalette(name));
        var surface = colors["PosSurfaceBrush"];

        Assert.True(Contrast(colors["PosTextBrush"], surface) >= 7, "正文对比度应达到 AAA。");
        Assert.True(Contrast(colors["PosMutedForegroundBrush"], surface) >= 4.5, "次要文字对比度不足。");
        Assert.True(Contrast(colors["PosPrimaryTextBrush"], surface) >= 4.5, "主色文字对比度应达到 AA。");
        // 各页面在主色、危险色填充上都用白字。
        Assert.True(Contrast(Colors.White, colors["PosPrimaryBrush"]) >= 4.5, "主色填充上的白字应达到 AA。");
        Assert.True(Contrast(Colors.White, colors["PosDangerBrush"]) >= 4.5, "危险色填充上的白字应达到 AA。");
    }

    [Fact]
    public void Themed_views_reference_palette_colors_dynamically_and_do_not_hard_code_colors()
    {
        var paletteKeys = BrushKeys(LoadPalette("Blue"));
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
                applier.Apply(PosColorTheme.Blue);
            }

            Assert.Equal("Blue", application.Resources["PosPaletteId"]);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(border.Background).Color);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Switcher_lists_eight_themes_with_blue_first_marks_the_current_one_and_closes_after_selection()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var service = new ColorThemeService(new MemorySettings(), new RecordingApplier());
        var switcher = new ColorThemeSwitcherViewModel(service, localization);

        Assert.Equal("界面配色", switcher.MenuTitle);
        Assert.Equal(["蓝色", "糖果紫", "阳光橙", "海洋青", "彩虹分区", "石墨夜", "青瓷", "长者模式"], switcher.Options.Select(option => option.Name));
        Assert.All(switcher.Options, option => Assert.False(string.IsNullOrWhiteSpace(option.Hint)));
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
        Assert.Equal(["Blue", "Candy", "Sunny", "Ocean", "Rainbow", "Graphite", "Celadon", "Easy View"], switcher.Options.Select(option => option.Name));
    }

    public static TheoryData<string> AllPalettes() => new(PaletteNames);

    [Theory]
    [MemberData(nameof(AllPalettes))]
    public void Sidebar_icons_are_readable_on_their_key_backgrounds(string name)
    {
        var colors = SolidColors(LoadPalette(name));
        var icon = colors["PosSidebarIconBrush"];
        var refund = colors["PosSidebarRefundIconBrush"];
        var hold = colors["PosSidebarHoldIconBrush"];

        if (name is "Candy" or "Sunny")
        {
            // 糖果紫 / 阳光橙：功能键白底，三种图标各用一种功能色（图形元素按 WCAG 非文字 3:1）。
            Assert.Equal(3, new[] { icon, refund, hold }.Distinct().Count());
            Assert.All(new[] { icon, refund, hold }, ink => Assert.True(Contrast(ink, colors["PosSidebarButtonBrush"]) >= 3, "侧栏彩色图标对比度不足。"));
        }
        else
        {
            Assert.Equal(icon, refund);
            Assert.Equal(icon, hold);
            // 长者 / 海洋青 / 彩虹分区的功能键是实心底配白图标，图标坐在按钮底色上而不是白卡片上。
            var iconBackgrounds = MulticolorSidebarPalettes.Contains(name)
                ? SidebarTileKeys.Select(key => colors[key]).ToArray()
                : [name == "Elder" ? colors["PosSidebarButtonBrush"] : colors["PosSurfaceBrush"]];
            Assert.All(iconBackgrounds, background => Assert.True(Contrast(icon, background) >= 4.5, "侧栏图标对比度应达到 AA。"));
        }
    }

    [Theory]
    [MemberData(nameof(AllPalettes))]
    public void Sidebar_tiles_get_one_color_per_key_only_in_the_multicolor_themes(string name)
    {
        var colors = SolidColors(LoadPalette(name));
        var tiles = SidebarTileKeys.Select(key => colors[key]).ToArray();

        if (!MulticolorSidebarPalettes.Contains(name))
        {
            // 其余主题的逐键底色都等于功能键底色，外观与改动前一致。
            Assert.All(tiles, tile => Assert.Equal(colors["PosSidebarButtonBrush"], tile));
            Assert.Equal(255, colors["PosSidebarButtonHoverBrush"].A);
            Assert.Equal(255, colors["PosSidebarButtonPressedBrush"].A);
            return;
        }

        // 每个功能键一种颜色，白字可读；悬停 / 按下是叠在键自身颜色上的半透明深色，而不是换成另一种颜色。
        Assert.Equal(tiles.Length, tiles.Distinct().Count());
        Assert.All(tiles, tile => Assert.True(Contrast(colors["PosSidebarButtonForegroundBrush"], tile) >= 4.5, $"{name}: 功能键白字对比度不足。"));
        Assert.True(Contrast(colors["PosSidebarExitForegroundBrush"], colors["PosSidebarExitButtonBrush"]) >= 4.5, "退出键文字对比度不足。");
        Assert.InRange(colors["PosSidebarButtonHoverBrush"].A, 1, 128);
        Assert.InRange(colors["PosSidebarButtonPressedBrush"].A, 1, 128);
        Assert.Equal(Colors.Black with { A = 0 }, colors["PosSidebarButtonHoverBrush"] with { A = 0 });
    }

    [Fact]
    public void Sidebar_buttons_paint_their_own_tile_color_and_the_template_keeps_it_under_hover()
    {
        var view = XDocument.Load(Path.Combine(WpfRoot(), "Views", "Screens", "PosTerminalView.xaml"));
        var sidebarButtons = view.Descendants(Presentation + "Button")
            .Where(button => ((string?)button.Attribute("Style"))?.Contains("PosSidebar", StringComparison.Ordinal) == true)
            .ToArray();
        var backgrounds = sidebarButtons
            .Select(button => (string?)button.Attribute("Background"))
            .Where(background => background is not null)
            .ToArray();

        // 10 个按钮本地写逐键底色；Card Recovery 走样式（要让警示触发器能覆盖），退出键保留自己的红色键。
        Assert.Equal(10, backgrounds.Length);
        var cardRecoveryStyle = view.Descendants(Presentation + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == "PosSidebarCardRecoveryButtonStyle");
        var cardRecoveryBackground = cardRecoveryStyle.Elements(Presentation + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == "Background");
        Assert.Equal(
            SidebarTileKeys.Select(key => $"{{DynamicResource {key}}}").Order(StringComparer.Ordinal),
            backgrounds.Append((string)cardRecoveryBackground.Attribute("Value")!).Order(StringComparer.Ordinal));
        Assert.Null(sidebarButtons.Single(button => (string?)button.Attribute("Command") == "{Binding OpenCardRecoveryCenterCommand}").Attribute("Background"));

        var template = view.Descendants(Presentation + "ControlTemplate")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "PosSidebarActionButtonTemplate");
        var layers = template.Element(Presentation + "Grid")!.Elements(Presentation + "Border").Select(border => (string?)border.Attribute(Xaml + "Name")).ToArray();
        Assert.Equal(["KeyShadow", "TileBase", "Root"], layers);
    }

    [Theory]
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
            // PosCartSelectedRowBrush 不在此列：购物车选中行底色已有意加深（见 Cart_selected_row_stands_out_from_neighbours_in_every_palette），不再等于 PosPrimarySoftBrush。
            ("PosDueAmountBrush", "PosAccentBrush"),
            ("PosClearKeyBrush", "PosSurfaceBrush"),
            ("PosNoBarcodeBrush", "PosPrimaryBrush"),
            ("PosNoBarcodePressedBrush", "PosPrimaryStrongBrush"),
            // 禁用态改成灰块灰字（原来是比可用态淡一点的蓝，不够明显）。
            ("PosNoBarcodeDisabledBrush", "PosSurfaceStrongBrush"),
            ("PosNoBarcodeDisabledForegroundBrush", "PosMutedForegroundBrush"),
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
            ("PosPopupBorderBrush", "PosDividerBrush"),
            ("PosSidebarWarnButtonBrush", "PosWarningSoftBrush"),
            ("PosSidebarWarnForegroundBrush", "PosWarningTextBrush"),
            ("PosSidebarWarnBorderBrush", "PosWarningBrush"),
            ("PosSidebarWarnHoverBrush", "PosWarningSoftBrush"),
            ("PosSidebarWarnPressedBrush", "PosWarningBorderBrush"),
            ("PosSidebarBadgeBrush", "PosPrimaryBrush"),
            ("PosSidebarBadgeAlertBrush", "PosDangerBrush")
        };

        foreach (var (key, legacy) in equivalents)
        {
            Assert.True(colors[key] == colors[legacy], $"{name}: {key} 应等于改版前的 {legacy}");
        }

        Assert.Equal(Colors.White, colors["PosNoBarcodeForegroundBrush"]);
        Assert.Equal(Colors.White, colors["PosSidebarBadgeForegroundBrush"]);
        // 投影键在旧主题里完全透明（不画任何厚度）。
        Assert.Equal(0, colors["PosKeyShadowBrush"].A);
        Assert.Equal(0, colors["PosConfirmPaymentShadowBrush"].A);
    }

    [Theory]
    [MemberData(nameof(AllPalettes))]
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
            // 付款方式方块未选态用加深的 Tile 底色（浅 Soft 底在白卡片上太淡，像失效），字同样要可读。
            var tile = colors[$"PosMethod{method}TileBrush"];
            if (Contrast(ink, tile) < 4.5) failures.Add($"{method} 方块底色上的字 {Contrast(ink, tile):F2}");
            if (Contrast(fill, surface) < 3) failures.Add($"{method} 实心块对卡片底色 {Contrast(fill, surface):F2}");
        }

        // 功能区警示键（刷卡恢复有待处理卡交易）与角标（挂单数量、二维码秒数、待处理数量）必须可读。
        if (Contrast(colors["PosSidebarWarnForegroundBrush"], colors["PosSidebarWarnButtonBrush"]) < 4.5) failures.Add("警示键文字");
        if (Contrast(colors["PosSidebarBadgeForegroundBrush"], colors["PosSidebarBadgeBrush"]) < 4.5) failures.Add("角标文字");
        if (Contrast(Colors.White, colors["PosSidebarBadgeAlertBrush"]) < 4.5) failures.Add("警示角标文字");

        foreach (var amount in new[] { 100, 50, 20, 10, 5 })
        {
            var fill = colors[$"PosNote{amount}FillBrush"];
            var text = colors[$"PosNote{amount}TextBrush"];
            if (Contrast(text, fill) < 4.5) failures.Add($"${amount} 面额数字 {Contrast(text, fill):F2}");
        }

        Assert.True(failures.Count == 0, $"{name}: " + string.Join("; ", failures));
    }

    [Theory]
    [MemberData(nameof(AllPalettes))]
    public void Cart_selected_row_stands_out_from_neighbours_in_every_palette(string name)
    {
        const double minimumNeighbourContrast = 1.5;
        // 选中行与上下相邻的普通行 / 斑马纹行必须拉开对比，收银员才能一眼找到当前行；
        // 同时正文仍要读得清、左侧色条要压得住选中底、选中的退货行里红字也要可读。
        var colors = SolidColors(LoadPalette(name));
        var selected = colors["PosCartSelectedRowBrush"];
        var failures = new List<string>();

        void Require(string label, double actual, double minimum)
        {
            if (actual < minimum) failures.Add($"{label} {actual:F2} < {minimum}");
        }

        Require("选中底 / 普通行", Contrast(selected, colors["PosSurfaceBrush"]), minimumNeighbourContrast);
        Require("选中底 / 斑马纹行", Contrast(selected, colors["PosCartAltRowBrush"]), minimumNeighbourContrast);
        Require("选中行正文", Contrast(colors["PosTextBrush"], selected), 7);
        Require("色条 / 选中底", Contrast(colors["PosCartSelectedBarBrush"], selected), 3);
        Require("选中退货行红字", Contrast(colors["PosDangerTextBrush"], colors["PosCartSelectedDangerRowBrush"]), 4.5);
        Assert.True(failures.Count == 0, $"{name}: " + string.Join("; ", failures));
    }

    [Theory]
    [MemberData(nameof(AllPalettes))]
    public void Title_bar_text_icons_and_button_states_are_readable_on_the_header_color(string name)
    {
        var colors = SolidColors(LoadPalette(name));
        var header = colors["PosHeaderBackgroundBrush"];
        var failures = new List<string>();

        void Require(string label, double actual, double minimum)
        {
            if (actual < minimum) failures.Add($"{label} {actual:F2} < {minimum}");
        }

        Require("标题栏文字", Contrast(colors["PosHeaderForegroundBrush"], header), 4.5);
        Require("标题栏次要文字", Contrast(colors["PosHeaderMutedForegroundBrush"], header), 4.5);
        Require("标题栏图标", Contrast(colors["PosHeaderIconBrush"], header), 4.5);
        Require("标题栏门店图标", Contrast(colors["PosHeaderAccentBrush"], header), 3);
        // 悬停 / 按下可能是半透明叠层（彩色主题）或不透明色（旧主题），都按叠在标题栏底色上的结果核对文字。
        Require("悬停态文字", Contrast(colors["PosHeaderForegroundBrush"], Over(colors["PosHeaderHoverBrush"], header)), 4.5);
        Require("按下态文字", Contrast(colors["PosHeaderForegroundBrush"], Over(colors["PosHeaderPressedBrush"], header)), 4.5);
        Assert.True(failures.Count == 0, $"{name}: " + string.Join("; ", failures));
    }

    [Theory]
    [InlineData("Blue")]
    [InlineData("Graphite")]
    [InlineData("Celadon")]
    [InlineData("Elder")]
    public void Classic_title_bars_keep_the_pre_redesign_colors(string name)
    {
        // 标题栏改用 PosHeader* 键之前直接取页面通用色；旧主题的取值必须等于当时实际显示的颜色，外观不变。
        var colors = SolidColors(LoadPalette(name));
        var equivalents = new (string Key, string Legacy)[]
        {
            ("PosHeaderBackgroundBrush", "PosSurfaceBrush"),
            ("PosHeaderForegroundBrush", "PosTextBrush"),
            ("PosHeaderMutedForegroundBrush", "PosTextSecondaryBrush"),
            ("PosHeaderBorderBrush", "PosDividerBrush"),
            ("PosHeaderDividerBrush", "PosSurfaceStrongBrush"),
            ("PosHeaderHoverBrush", "PosSurfaceMutedBrush"),
            ("PosHeaderPressedBrush", "PosSurfaceStrongBrush"),
            ("PosHeaderIconBrush", "PosMutedForegroundBrush"),
            ("PosHeaderAccentBrush", "PosPrimaryTextBrush")
        };

        foreach (var (key, legacy) in equivalents)
        {
            Assert.True(colors[key] == colors[legacy], $"{name}: {key} 应等于改版前的 {legacy}");
        }
    }

    [Theory]
    [InlineData("Candy")]
    [InlineData("Sunny")]
    [InlineData("Ocean")]
    [InlineData("Rainbow")]
    public void Vivid_themes_have_a_colored_title_bar_with_white_text(string name)
    {
        var colors = SolidColors(LoadPalette(name));

        Assert.NotEqual(colors["PosSurfaceBrush"], colors["PosHeaderBackgroundBrush"]);
        Assert.Equal(Colors.White, colors["PosHeaderForegroundBrush"]);
        // 悬停 / 按下是叠在标题栏底色上的半透明深色，不会换成别的颜色。
        Assert.InRange(colors["PosHeaderHoverBrush"].A, 1, 128);
        Assert.InRange(colors["PosHeaderPressedBrush"].A, 1, 128);
        // 关闭按钮悬停用浅红底红字小块，在彩色标题栏上也要可读。
        Assert.True(Contrast(colors["PosDangerTextBrush"], colors["PosDangerSoftBrush"]) >= 4.5, "关闭按钮悬停的红字对比度不足。");
    }

    [Fact]
    public void Title_bar_uses_header_palette_keys_and_leaves_the_shared_button_styles_alone()
    {
        var window = XDocument.Load(Path.Combine(WpfRoot(), "MainWindow.xaml"));
        var titleBar = Assert.Single(window.Descendants(Presentation + "Border").Where(border =>
            (string?)border.Attribute("MouseLeftButtonDown") == "TitleBar_MouseLeftButtonDown"));
        Assert.Equal("{DynamicResource PosHeaderBackgroundBrush}", (string?)titleBar.Attribute("Background"));
        Assert.Equal("{DynamicResource PosHeaderBorderBrush}", (string?)titleBar.Attribute("BorderBrush"));

        var styleOf = (XElement button) => ((string?)button.Attribute("Style"))?.Replace("{StaticResource ", string.Empty).TrimEnd('}');
        var buttons = titleBar.Descendants(Presentation + "Button").ToArray();
        string?[] titleBarButtonStyles = buttons.Select(styleOf).ToArray();
        // 语言、扫码枪用标题栏专用工具按钮；最小化 / 最大化 / 关闭用标题栏专用窗口按钮。
        Assert.Equal(2, titleBarButtonStyles.Count(style => style == "HeaderBarUtilityButtonStyle"));
        Assert.Equal(2, titleBarButtonStyles.Count(style => style == "HeaderWindowButtonStyle"));
        Assert.Equal(1, titleBarButtonStyles.Count(style => style == "HeaderCloseButtonStyle"));
        // 页脚同步中心、各弹窗关闭按钮仍用页面通用样式；标题栏里不能再出现它们（深色标题栏上悬停色会糊）。
        Assert.DoesNotContain("HeaderUtilityButtonStyle", titleBarButtonStyles);
        Assert.DoesNotContain("TitleBarButtonStyle", titleBarButtonStyles);
        Assert.DoesNotContain("TitleBarCloseButtonStyle", titleBarButtonStyles);

        // 全局隐式 TextBlock 样式会把文字色固定成 PosTextBrush，盖掉按钮继承的前景色；
        // 标题栏工具按钮必须自己声明让文字跟随按钮前景色（否则彩色标题栏上「中 / EN」「扫码枪」是深色字）。
        const string followButtonForeground = "{Binding Foreground, RelativeSource={RelativeSource AncestorType=ButtonBase}}";
        var utilityStyle = window.Descendants(Presentation + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == "HeaderBarUtilityButtonStyle");
        var styleResourcesTextBlock = utilityStyle.Element(Presentation + "Style.Resources")!.Elements(Presentation + "Style").Single();
        var presenterTextBlock = utilityStyle.Descendants(Presentation + "ContentPresenter.Resources").Single().Elements(Presentation + "Style").Single();
        Assert.All(new[] { styleResourcesTextBlock, presenterTextBlock }, textStyle =>
        {
            Assert.Equal("TextBlock", (string?)textStyle.Attribute("TargetType"));
            Assert.Equal(followButtonForeground, (string?)textStyle.Elements(Presentation + "Setter").Single(setter => (string?)setter.Attribute("Property") == "Foreground").Attribute("Value"));
        });

        // 标题栏自己的文字（品牌名、页面标题）取标题栏前景色，不再取页面正文色。
        var directTexts = titleBar.Descendants(Presentation + "TextBlock")
            .Where(text => text.Ancestors(Presentation + "Popup").Any() == false)
            .Select(text => (string?)text.Attribute("Foreground"))
            .Where(foreground => foreground is not null);
        Assert.All(directTexts, foreground => Assert.Equal("{DynamicResource PosHeaderForegroundBrush}", foreground));
    }

    private static Color Over(Color top, Color bottom)
    {
        var alpha = top.A / 255d;
        byte Mix(byte over, byte under) => (byte)Math.Round((over * alpha) + (under * (1 - alpha)));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
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
        Require("Open Item 禁用态文字", "PosNoBarcodeDisabledForegroundBrush", "PosNoBarcodeDisabledBrush", 4.5);
        // 可用（深蓝）与禁用（浅灰）的底色必须一眼分得开。
        Assert.True(Contrast(colors["PosNoBarcodeBrush"], colors["PosNoBarcodeDisabledBrush"]) >= 4.5, "Open Item 可用 / 禁用底色区分度不足。");
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
    public void Sidebar_labels_have_room_for_two_natural_text_lines_in_every_metrics_set()
    {
        // WPF 的 TextBlock 默认按字体自然行高（≈ 字号×1.33）叠行，LineHeight 并不生效：
        // MaxHeight 小于两行的自然高度时，第二行会被截掉，英文「Special Items」「Hold Order」只剩首词加省略号（真机截图发现）。
        // 标准（旧五套主题共用）与长者两套尺寸都要按各自字号核对。
        foreach (var file in new[] { "Home.Standard.xaml", "Home.Elder.xaml" })
        {
            var metrics = XDocument.Load(Path.Combine(WpfRoot(), "Themes", "Metrics", file));
            double Value(string key) => double.Parse(
                metrics.Descendants().Single(element => (string?)element.Attribute(Xaml + "Key") == key).Value.Trim(),
                CultureInfo.InvariantCulture);

            Assert.True(
                Value("PosHomeSidebarLabelMaxHeight") >= 2 * Value("PosHomeSidebarLabelFontSize") * 1.34,
                $"{file} 功能区标签的 MaxHeight 装不下两行文字。");
        }
    }

    [Fact]
    public void Elder_keyboard_column_needs_no_more_height_than_standard_and_digits_fit_their_keys()
    {
        // 真机（1366×768 带任务栏，窗口只有约 728 高）上，长者主题曾因输入框/操作区/折扣条加高、数字键 38 号字，
        // 把 4 行数字键压到约 50px 高，数字下半截被切掉。键盘列的固定高度总和不能超过标准，数字键字号的自然行高
        // （≈ 字号×1.34）必须小于按 50px 键高留出的余量。
        double Sum(string file, Func<Func<string, double>, double> total)
        {
            var doc = XDocument.Load(Path.Combine(WpfRoot(), "Themes", "Metrics", file));
            double Value(string key) => double.Parse(
                doc.Descendants().Single(element => (string?)element.Attribute(Xaml + "Key") == key).Value.Trim(),
                CultureInfo.InvariantCulture);
            return total(Value);
        }

        double Fixed(Func<string, double> v) =>
            v("PosHomeInputBoxHeight") + v("PosHomeNoBarcodeHeight") + 2 * v("PosHomeOpsButtonHeight") + v("PosHomeDiscountHeight");

        Assert.True(
            Sum("Home.Elder.xaml", Fixed) <= Sum("Home.Standard.xaml", Fixed),
            "长者主题键盘列的固定高度总和超过标准，会把数字键压扁。");
        Assert.True(
            Sum("Home.Elder.xaml", v => v("PosHomeKeyFontSize")) * 1.34 <= 42,
            "长者主题数字键字号过大，在约 50px 高的键里会被截掉下半截。");
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
                applier.Apply(PosColorTheme.Blue);
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
        // 12 个功能键（含 Card Recovery / Attendance QR）；Attendance QR 走 Click 事件、没有 Command，改用 x:Name 当键。
        Assert.Equal(12, sidebarButtons.Length);

        var iconColors = sidebarButtons.ToDictionary(
            button => (string?)button.Attribute("Command") ?? (string)button.Attribute(Xaml + "Name")!,
            button => (string?)button.Descendants(materialDesign + "PackIcon").Single().Attribute("Foreground"));

        Assert.Equal("{DynamicResource PosSidebarRefundIconBrush}", iconColors["{Binding OpenReturnsCommand}"]);
        Assert.Equal("{DynamicResource PosSidebarHoldIconBrush}", iconColors["{Binding HoldOrderCommand}"]);
        // 退出图标改走 PosSidebarExitForegroundBrush：旧主题取值＝PosDangerTextBrush（仍是危险红），长者主题是红底上的白色。
        Assert.Equal("{DynamicResource PosSidebarExitForegroundBrush}", iconColors["{Binding ExitApplicationCommand}"]);
        // Card Recovery 的图标颜色随「有待处理卡交易」切换，只能走样式（本地值会盖过样式触发器），所以没有 Foreground 属性。
        Assert.Null(iconColors["{Binding OpenCardRecoveryCenterCommand}"]);
        Assert.Equal("{DynamicResource PosSidebarIconBrush}", iconColors["AttendanceQrLauncher"]);
        Assert.All(
            iconColors.Where(pair => pair.Key is not (
                "{Binding OpenReturnsCommand}" or "{Binding HoldOrderCommand}" or "{Binding ExitApplicationCommand}"
                or "{Binding OpenCardRecoveryCenterCommand}")),
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
    public void Title_bar_buttons_show_the_current_theme_and_customer_display_state()
    {
        var window = XDocument.Load(Path.Combine(WpfRoot(), "MainWindow.xaml"));
        var texts = window.Descendants(Presentation + "TextBlock")
            .Select(text => (string?)text.Attribute("Text"))
            .ToList();

        Assert.Contains("{Binding CurrentThemeName}", texts);
        Assert.Contains("{Binding CustomerDisplayStateText}", texts);
    }

    [Fact]
    public void Footer_shows_store_info_right_before_the_terminal_code()
    {
        var window = XDocument.Load(Path.Combine(WpfRoot(), "MainWindow.xaml"));
        var store = Assert.Single(window.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("AutomationProperties.AutomationId") == "CurrentStoreInfo");
        Assert.Equal("{Binding StoreInfo}", (string?)store.Attribute("Text"));

        // 底栏：分店名称和代码紧挨在终端代码之前，同一个容器内；标题栏不再显示分店。
        var terminal = store.ElementsAfterSelf(Presentation + "TextBlock").First();
        Assert.Equal("{Binding TerminalInfo}", (string?)terminal.Attribute("Text"));
        var footer = store.Ancestors(Presentation + "Border")
            .First(border => (string?)border.Attribute("Grid.Row") == "2");
        Assert.NotNull(footer);
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
