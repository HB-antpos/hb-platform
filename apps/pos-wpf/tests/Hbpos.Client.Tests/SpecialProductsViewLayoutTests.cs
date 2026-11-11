using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

/// <summary>
/// 特殊商品页重设计的结构契约：直接解析 XAML，不需要 WPF 宿主。
/// 覆盖：浏览 / 编辑两种模式的区域划分、可滚动网格、触控目标与字号下限、命令与可见性绑定。
/// </summary>
public sealed class SpecialProductsViewLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Workspace_has_browse_cart_rail_and_edit_side_panel_with_mode_visibility()
    {
        var view = LoadView();
        var workspace = FindNamedElement(view, "SpecialProductsWorkspace");
        Assert.Equal("{DynamicResource PosScreenBackgroundBrush}", (string?)workspace.Attribute("Background"));

        // 浏览模式：右侧是购物车摘要，用「仅浏览模式显示」命名样式，编辑模式自动收起。
        var cartRail = FindNamedElement(view, "SpecialCartRail");
        Assert.Equal("296", (string?)cartRail.Attribute("Width"));
        Assert.Equal("{StaticResource SpecialBrowseOnlyPanelStyle}", (string?)cartRail.Attribute("Style"));
        var browseOnlyPanel = FindStyle(view, "SpecialBrowseOnlyPanelStyle");
        Assert.Contains(browseOnlyPanel.Descendants(Presentation + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding IsEditMode}" &&
            (string?)trigger.Attribute("Value") == "True" &&
            trigger.Elements(Presentation + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "Visibility" &&
                (string?)setter.Attribute("Value") == "Collapsed"));

        // 编辑模式：右侧换成「添加商品」面板。
        var editPanel = FindNamedElement(view, "SpecialEditSidePanel");
        Assert.Equal("360", (string?)editPanel.Attribute("Width"));
        Assert.Equal(
            "{Binding IsEditMode, Converter={StaticResource BoolToVis}}",
            (string?)editPanel.Attribute("Visibility"));

        // 编辑横幅与选中项操作条只在编辑模式出现。
        foreach (var name in new[] { "SpecialEditBanner", "SpecialEditActionBar" })
        {
            Assert.Equal(
                "{Binding IsEditMode, Converter={StaticResource BoolToVis}}",
                (string?)FindNamedElement(view, name).Attribute("Visibility"));
        }

        // 状态筛选标签只在浏览模式出现。
        Assert.Equal(
            "{StaticResource SpecialBrowseOnlyGridStyle}",
            (string?)FindNamedElement(view, "SpecialFilterBar").Attribute("Style"));

        // 下载进度、加购提示、改价弹窗各自由独立的状态开关控制。
        Assert.Equal(
            "{Binding IsDownloadProgressVisible, Converter={StaticResource BoolToVis}}",
            (string?)FindNamedElement(view, "SpecialDownloadProgressPanel").Attribute("Visibility"));
        Assert.Equal(
            "{Binding IsToastVisible, Converter={StaticResource BoolToVis}}",
            (string?)FindNamedElement(view, "SpecialToast").Attribute("Visibility"));
        Assert.Equal(
            "{Binding IsPriceEditorOpen, Converter={StaticResource BoolToVis}}",
            (string?)FindNamedElement(view, "SpecialPriceEditor").Attribute("Visibility"));
    }

    [Fact]
    public void Product_grid_scrolls_by_touch_wraps_cards_and_keeps_card_interactions()
    {
        var view = LoadView();
        var grid = FindNamedElement(view, "SpecialProductGrid");
        Assert.Equal("ItemsControl", grid.Name.LocalName);
        Assert.Equal("{Binding DisplayedCards}", (string?)grid.Attribute("ItemsSource"));

        // 不再分页：卡片用 WrapPanel 自适应换行（不再是固定 4×5 的 UniformGrid），外层是触控垂直滚动容器。
        var itemsPanel = grid.Element(Presentation + "ItemsControl.ItemsPanel")!;
        Assert.Empty(itemsPanel.Descendants(Presentation + "UniformGrid"));
        var wrap = Assert.Single(itemsPanel.Descendants(Presentation + "WrapPanel"));
        Assert.Contains("CardItemWidth", (string?)wrap.Attribute("ItemWidth"), StringComparison.Ordinal);
        Assert.Contains("CardItemHeight", (string?)wrap.Attribute("ItemHeight"), StringComparison.Ordinal);
        var scroller = Assert.Single(view.Descendants(Presentation + "ScrollViewer").Where(element =>
            element.Descendants().Any(child => ReferenceEquals(child, grid))));
        Assert.Equal("{StaticResource PosVerticalTouchScrollViewerStyle}", (string?)scroller.Attribute("Style"));

        var cardButton = Assert.Single(grid.Descendants(Presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("SpecialItemCardCommand", StringComparison.Ordinal) == true));
        Assert.Equal("{Binding Item}", (string?)cardButton.Attribute("CommandParameter"));

        var image = Assert.Single(cardButton.Descendants(Presentation + "Image"));
        Assert.Contains(image.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".AsyncSourceText", StringComparison.Ordinal) &&
            attribute.Value == "{Binding ProductImage}");
        Assert.Contains(cardButton.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding DisplayName}");
        Assert.Contains(cardButton.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding LookupCode}");
        var price = Assert.Single(cardButton.Descendants(Presentation + "TextBlock").Where(text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding RetailPrice", StringComparison.Ordinal) == true));
        Assert.Equal("{DynamicResource PosAccentBrush}", (string?)price.Attribute("Foreground"));

        // 购物车数量角标跟随 IsInCart。
        Assert.Contains(grid.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding CartQuantityText}");
        Assert.Contains(grid.Descendants(Presentation + "Border"), border =>
            (string?)border.Attribute("Visibility") == "{Binding IsInCart, Converter={StaticResource BoolToVis}}");

        // 选中 / 刚加入都用 2px 主色描边加浅底；已在购物车只换描边色。
        var triggers = grid.Descendants(Presentation + "DataTrigger")
            .Where(trigger => trigger.Elements(Presentation + "Setter").Any(setter =>
                (string?)setter.Attribute("TargetName") == "SpecialProductCard"))
            .ToArray();
        foreach (var binding in new[] { "{Binding IsSelected}", "{Binding IsJustAdded}" })
        {
            var trigger = Assert.Single(triggers.Where(item => (string?)item.Attribute("Binding") == binding));
            AssertTriggerSetter(trigger, "BorderBrush", "{DynamicResource PosPrimaryBrush}");
            AssertTriggerSetter(trigger, "BorderThickness", "2");
            AssertTriggerSetter(trigger, "Background", "{DynamicResource PosPrimarySoftBrush}");
        }

        Assert.Single(triggers.Where(item => (string?)item.Attribute("Binding") == "{Binding IsInCart}"));
    }

    [Fact]
    public void Touch_targets_and_type_scale_meet_the_redesign_floor()
    {
        var view = LoadView();

        // 按钮基样式不低于 48，主按钮 56，键盘键 60。
        AssertSetter(FindStyle(view, "SpecialFlatButtonBaseStyle"), "MinHeight", "48");
        AssertSetter(FindStyle(view, "SpecialPrimaryButtonStyle"), "MinHeight", "56");
        AssertSetter(FindStyle(view, "SpecialKeypadKeyStyle"), "MinHeight", "60");
        AssertSetter(FindStyle(view, "SpecialIconButtonStyle"), "MinWidth", "48");

        // 卡片内联按钮（移除确认）允许 44，其余显式 MinHeight 一律不得低于 44。
        var explicitHeights = view.Descendants(Presentation + "Button")
            .Select(button => (string?)button.Attribute("MinHeight"))
            .Where(value => value is not null)
            .Select(value => double.Parse(value!, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.All(explicitHeights, height => Assert.True(height >= 44, $"按钮 MinHeight {height} 小于 44。"));

        // 字号只用设计规定的五档，且最小 12。
        var text = File.ReadAllText(ViewPath());
        var fontSizes = Regex.Matches(text, "FontSize=\"(\\d+)\"")
            .Select(match => int.Parse(match.Groups[1].Value))
            .Distinct()
            .OrderBy(size => size)
            .ToArray();
        Assert.Equal([12, 14, 16, 22, 32], fontSizes);
    }

    [Fact]
    public void Commands_and_bindings_cover_browse_edit_and_price_editor()
    {
        var view = LoadView();
        var commands = view.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "Command")
            .Select(attribute => attribute.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var expectedCommands = new[]
        {
            "{Binding BackCommand}",
            "{Binding BackCommand}",
            "{Binding BeginRemoveCommand}",
            "{Binding ClearSearchCommand}",
            "{Binding ClosePriceEditorCommand}",
            "{Binding ClosePriceEditorCommand}",
            "{Binding DataContext.AddSpecialProductCommand, RelativeSource={RelativeSource AncestorType=ListBox}}",
            "{Binding DataContext.CancelRemoveCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}",
            "{Binding DataContext.PickCategoryCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}",
            "{Binding DataContext.RemoveSpecialProductCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}",
            "{Binding DataContext.SelectCategoryCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}",
            "{Binding DataContext.SpecialItemCardCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}",
            "{Binding CloseCategoryPickerCommand}",
            "{Binding DownloadCommand}",
            "{Binding MoveDownCommand}",
            "{Binding MoveToFrontCommand}",
            "{Binding MoveUpCommand}",
            "{Binding OpenCategoryPickerCommand}",
            "{Binding OpenPriceEditorCommand}",
            "{Binding PriceBackspaceCommand}",
            "{Binding PriceClearCommand}",
            "{Binding RefreshCommand}",
            "{Binding SavePriceCommand}",
            "{Binding SearchCommand}",
            "{Binding SearchCommand}",
            "{Binding ShowAllCommand}",
            "{Binding ShowInCartCommand}",
            "{Binding ShowMoreCommand}",
            "{Binding ToggleEditModeCommand}",
            "{Binding ToggleEditModeCommand}",
            "{Binding UndoAddCommand}",
        }.Concat(Enumerable.Repeat("{Binding PriceKeyCommand}", 11))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedCommands, commands);

        // 选中项操作条的按钮都作用于当前选中商品；改价是其中之一。
        var actionBar = FindNamedElement(view, "SpecialEditActionBar");
        foreach (var command in new[]
                 {
                     "MoveToFrontCommand", "MoveUpCommand", "MoveDownCommand",
                     "OpenPriceEditorCommand", "BeginRemoveCommand",
                 })
        {
            var button = Assert.Single(actionBar.Descendants(Presentation + "Button").Where(element =>
                ((string?)element.Attribute("Command"))?.Contains(command, StringComparison.Ordinal) == true));
            Assert.Equal("{Binding SelectedSpecialItem}", (string?)button.Attribute("CommandParameter"));
        }

        // 移除只有在卡片内二次确认后才真正执行。
        var confirmRemove = Assert.Single(view.Descendants(Presentation + "Button").Where(element =>
            ((string?)element.Attribute("Command"))?.Contains("RemoveSpecialProductCommand", StringComparison.Ordinal) == true));
        Assert.Equal("{Binding Item}", (string?)confirmRemove.Attribute("CommandParameter"));
        Assert.Contains(confirmRemove.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".Cue", StringComparison.Ordinal) &&
            attribute.Value == "Delete");

        var downloadButton = Assert.Single(view.Descendants(Presentation + "Button").Where(element =>
            (string?)element.Attribute("Command") == "{Binding DownloadCommand}"));
        Assert.Contains(downloadButton.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".Cue", StringComparison.Ordinal) &&
            attribute.Value == "Download");

        // 编辑模式的添加面板仍是搜索结果 + 「添加」按钮。
        var editSearch = FindNamedElement(view, "SpecialEditSearchPanel");
        Assert.Contains(editSearch.Descendants(Presentation + "TextBox"), textBox =>
            (string?)textBox.Attribute("Text") == "{Binding SearchText, UpdateSourceTrigger=PropertyChanged}");
        var results = Assert.Single(view.Descendants(Presentation + "ListBox").Where(list =>
            (string?)list.Attribute("ItemsSource") == "{Binding SearchResults}"));
        Assert.Equal("{Binding SelectedSearchResult, Mode=TwoWay}", (string?)results.Attribute("SelectedItem"));
        var addButton = Assert.Single(results.Descendants(Presentation + "Button").Where(element =>
            ((string?)element.Attribute("Command"))?.Contains("AddSpecialProductCommand", StringComparison.Ordinal) == true));
        Assert.Equal("{Binding}", (string?)addButton.Attribute("CommandParameter"));

        // 改价键盘：0–9 与小数点共 11 个键，参数是单字符字符串。
        var editor = FindNamedElement(view, "SpecialPriceEditor");
        var keyParameters = editor.Descendants(Presentation + "Button")
            .Where(button => (string?)button.Attribute("Command") == "{Binding PriceKeyCommand}")
            .Select(button => (string?)button.Attribute("CommandParameter"))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([".", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"], keyParameters);

        // 弹窗展示的关键信息都来自视图模型：商品名、当前价、新价、错误、联网提示、人工定价提示。
        var editorBindings = editor.Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .ToArray();
        foreach (var binding in new[]
                 {
                     "{Binding PriceEditorProductName}", "{Binding PriceEditorCurrentPriceText}",
                     "{Binding PriceEditorNewPriceText}", "{Binding PriceEditorErrorText}",
                     "{Binding PriceEditorOnlineHintText}", "{Binding PriceEditorManualNoteText}",
                 })
        {
            Assert.Contains(binding, editorBindings);
        }
    }

    [Fact]
    public void Category_bar_sits_left_of_the_grid_panel_and_only_shows_when_categories_exist()
    {
        var view = LoadView();
        var bar = FindNamedElement(view, "SpecialCategoryBar");

        // 分类栏宽 188，没有分类时整栏不显示（网格占满，与旧版一致）。
        Assert.Equal("188", (string?)bar.Attribute("Width"));
        Assert.Equal("{Binding HasCategories, Converter={StaticResource BoolToVis}}", (string?)bar.Attribute("Visibility"));
        Assert.Equal("{StaticResource SpecialPanelStyle}", (string?)bar.Attribute("Style"));

        // 主体三列：分类栏（Auto）| 商品网格（*）| 右侧购物车 / 添加面板（Auto）；分类栏是第 0 列，网格面板是第 1 列。
        var body = bar.Parent!;
        var columns = body.Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .Select(column => (string)column.Attribute("Width")!)
            .ToArray();
        Assert.Equal(["Auto", "*", "Auto"], columns);
        Assert.Equal("0", (string?)bar.Attribute("Grid.Column"));
        var gridPanel = body.Elements(Presentation + "Border").Single(border =>
            border.Descendants().Any(child => (string?)child.Attribute(Xaml + "Name") == "SpecialProductGrid"));
        Assert.Equal("1", (string?)gridPanel.Attribute("Grid.Column"));
        Assert.Equal("2", (string?)FindNamedElement(view, "SpecialCartRail").Parent!.Attribute("Grid.Column"));

        // 分类多了在栏内触控滚动。
        var scroller = Assert.Single(bar.Descendants(Presentation + "ScrollViewer"));
        Assert.Equal("{StaticResource PosVerticalTouchScrollViewerStyle}", (string?)scroller.Attribute("Style"));
        var list = FindNamedElement(view, "SpecialCategoryList");
        Assert.Equal("{Binding CategoryEntries}", (string?)list.Attribute("ItemsSource"));
    }

    [Fact]
    public void Category_rows_follow_the_touch_rules_and_use_theme_resources_for_selection()
    {
        var view = LoadView();
        var list = FindNamedElement(view, "SpecialCategoryList");

        // 行高 48（按钮基样式下限，行样式显式声明），字号 16（名称）/ 14（数量）。
        var rowStyle = FindStyle(view, "SpecialCategoryRowButtonStyle");
        AssertSetter(rowStyle, "MinHeight", "48");
        var row = Assert.Single(list.Descendants(Presentation + "Button"));
        Assert.Equal("{StaticResource SpecialCategoryRowButtonStyle}", (string?)row.Attribute("Style"));
        Assert.Equal("{Binding}", (string?)row.Attribute("CommandParameter"));
        var name = Assert.Single(row.Descendants(Presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "{Binding Name}"));
        Assert.Equal("16", (string?)name.Attribute("FontSize"));
        var count = Assert.Single(row.Descendants(Presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "{Binding CountText}"));
        Assert.Equal("14", (string?)count.Attribute("FontSize"));

        // 选中项：主题浅色底 + 左侧主题色条，全部是 DynamicResource，跟随 8 套主题。
        var trigger = Assert.Single(list.Descendants(Presentation + "DataTrigger").Where(item =>
            (string?)item.Attribute("Binding") == "{Binding IsSelected}"));
        AssertTriggerSetter(trigger, "Background", "{DynamicResource PosPrimarySoftBrush}");
        AssertTriggerSetter(trigger, "Visibility", "Visible");
        var selectedBar = FindNamedElement(view, "SpecialCategorySelectedBar");
        Assert.Equal("4", (string?)selectedBar.Attribute("Width"));
        Assert.Equal("{DynamicResource PosPrimaryBrush}", (string?)selectedBar.Attribute("Background"));

        // 色点：中性底点 + 分类自己的颜色点（来自视图模型已校验的 #RRGGBB），没有颜色时只显示中性点。
        Assert.Contains(list.Descendants(Presentation + "Ellipse"), ellipse =>
            (string?)ellipse.Attribute("Fill") == "{Binding ColorHex}" &&
            (string?)ellipse.Attribute("Visibility") == "{Binding HasColor, Converter={StaticResource BoolToVis}}");
    }

    [Fact]
    public void Filter_bar_keeps_its_two_chips_and_the_first_one_shows_the_current_category()
    {
        var view = LoadView();
        var bar = FindNamedElement(view, "SpecialFilterBar");

        // 第一个标签仍绑 AllChipText（有分类时由视图模型改成「分类名 + 数量」），第二个「已在购物车」不变。
        var chips = bar.Descendants(Presentation + "Button").ToArray();
        Assert.Equal(2, chips.Length);
        Assert.Equal("{Binding ShowAllCommand}", (string?)chips[0].Attribute("Command"));
        Assert.Equal("{Binding AllChipText}", (string?)chips[0].Attribute("Content"));
        Assert.Equal("{Binding ShowInCartCommand}", (string?)chips[1].Attribute("Command"));
        Assert.Equal("{Binding InCartChipText}", (string?)chips[1].Attribute("Content"));
    }

    [Fact]
    public void Edit_action_bar_has_a_move_to_category_button_that_only_shows_with_categories()
    {
        var view = LoadView();
        var actionBar = FindNamedElement(view, "SpecialEditActionBar");
        var button = FindNamedElement(view, "SpecialMoveToCategoryButton");

        Assert.Contains(actionBar.Descendants(), element => ReferenceEquals(element, button));
        Assert.Equal("{Binding OpenCategoryPickerCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("{Binding SelectedSpecialItem}", (string?)button.Attribute("CommandParameter"));
        Assert.Equal("{Binding HasCategories, Converter={StaticResource BoolToVis}}", (string?)button.Attribute("Visibility"));
        Assert.Equal("14", (string?)button.Attribute("FontSize"));
        Assert.Contains(button.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding MoveToCategoryText}");

        // 有分类时分类栏占去一列宽度，按钮组换到第二行（样式触发器改 Grid.Row）；没有分类时保持原来的一行。
        var buttons = FindNamedElement(view, "SpecialEditActionButtons");
        var trigger = Assert.Single(buttons.Descendants(Presentation + "DataTrigger"));
        Assert.Equal("{Binding HasCategories}", (string?)trigger.Attribute("Binding"));
        AssertTriggerSetter(trigger, "Grid.Row", "1");
        // 位置只能由样式 Setter 给出（本地属性会压过触发器）。
        Assert.Null(buttons.Attribute("Grid.Row"));
        Assert.Null(buttons.Attribute("Grid.Column"));
    }

    [Fact]
    public void Category_picker_lists_choices_with_a_check_mark_and_meets_touch_rules()
    {
        var view = LoadView();
        var picker = FindNamedElement(view, "SpecialCategoryPicker");

        Assert.Equal(
            "{Binding IsCategoryPickerOpen, Converter={StaticResource BoolToVis}}",
            (string?)picker.Attribute("Visibility"));
        var choices = FindNamedElement(view, "SpecialCategoryChoices");
        Assert.Equal("{Binding CategoryChoices}", (string?)choices.Attribute("ItemsSource"));
        var choice = Assert.Single(choices.Descendants(Presentation + "Button"));
        Assert.Equal("{Binding}", (string?)choice.Attribute("CommandParameter"));
        Assert.True(double.Parse((string)choice.Attribute("MinHeight")!, System.Globalization.CultureInfo.InvariantCulture) >= 44);
        // 当前所在分类打勾。
        var check = Assert.Single(choice.Descendants().Where(element => (string?)element.Attribute("Kind") == "Check"));
        Assert.Equal("{Binding IsCurrent, Converter={StaticResource BoolToVis}}", (string?)check.Attribute("Visibility"));
        Assert.Contains(picker.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding CategoryPickerTitleText}");
        Assert.Contains(picker.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding CategoryPickerProductName}");
        // 选项多时在弹层内触控滚动。
        var scroller = Assert.Single(picker.Descendants(Presentation + "ScrollViewer"));
        Assert.Equal("{StaticResource PosVerticalTouchScrollViewerStyle}", (string?)scroller.Attribute("Style"));
    }

    [Fact]
    public void Category_ui_uses_theme_resources_and_no_hard_coded_colors()
    {
        var view = LoadView();
        var regions = new[]
        {
            FindNamedElement(view, "SpecialCategoryBar"),
            FindNamedElement(view, "SpecialCategoryPicker"),
            FindNamedElement(view, "SpecialMoveToCategoryButton"),
            FindStyle(view, "SpecialCategoryRowButtonStyle"),
        };

        foreach (var region in regions)
        {
            foreach (var attribute in region.DescendantsAndSelf().SelectMany(element => element.Attributes()))
            {
                // 颜色一律走 DynamicResource（Transparent 例外）；不得出现 #RRGGBB 之类的字面颜色，
                // 分类自己的色点只允许绑定视图模型里已校验的 ColorHex。
                Assert.DoesNotMatch("^#[0-9A-Fa-f]{3,8}$", attribute.Value);
                if (attribute.Name.LocalName is "Background" or "Foreground" or "Fill" or "BorderBrush")
                {
                    Assert.True(
                        attribute.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal) ||
                        attribute.Value == "{Binding ColorHex}" ||
                        attribute.Value == "Transparent",
                        $"{attribute.Name.LocalName}={attribute.Value} 不是主题资源。");
                }
            }
        }
    }

    [Fact]
    public void View_has_no_hard_coded_user_visible_text()
    {
        // 所有文案都来自视图模型本地化属性，避免新增页面再把中文写死在 XAML 里。
        var text = File.ReadAllText(ViewPath());
        var literalText = Regex.Matches(text, "(?:Text|Content|ToolTip)=\"([^\"{][^\"]*)\"")
            .Select(match => match.Groups[1].Value)
            .Where(value => value.Any(char.IsLetter) && value.Any(character => character > 127))
            .ToArray();
        Assert.Empty(literalText);
    }

    private static string ViewPath() => Path.Combine(
        FindRepoRoot(),
        "apps",
        "pos-wpf",
        "src",
        "Hbpos.Client.Wpf",
        "Views",
        "Screens",
        "SpecialProductsView.xaml");

    private static XDocument LoadView() => XDocument.Load(ViewPath());

    private static XElement FindNamedElement(XDocument document, string name) =>
        Assert.Single(document.Descendants().Where(element =>
            (string?)element.Attribute(Xaml + "Name") == name));

    private static XElement FindStyle(XDocument document, string key) =>
        Assert.Single(document.Descendants(Presentation + "Style").Where(element =>
            (string?)element.Attribute(Xaml + "Key") == key));

    private static void AssertSetter(XElement style, string property, string value) =>
        Assert.Contains(style.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == property &&
            (string?)setter.Attribute("Value") == value);

    private static void AssertTriggerSetter(XElement trigger, string property, string value) =>
        Assert.Contains(trigger.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == property &&
            (string?)setter.Attribute("Value") == value);

    private static string FindRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        foreach (var start in new[]
                 {
                     Path.GetDirectoryName(sourceFilePath),
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                 })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "apps", "pos-wpf")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
