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
            "{Binding DataContext.RemoveSpecialProductCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}",
            "{Binding DataContext.SpecialItemCardCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}",
            "{Binding DownloadCommand}",
            "{Binding MoveDownCommand}",
            "{Binding MoveToFrontCommand}",
            "{Binding MoveUpCommand}",
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
