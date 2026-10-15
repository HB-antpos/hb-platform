using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class PosTerminalViewLayoutTests
{
    [Fact]
    public void Pos_terminal_middle_controls_use_consistent_touch_and_brand_styles()
    {
        var repoRoot = FindRepoRoot();
        var view = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PosTerminalView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var backspaceButton = Assert.Single(view.Descendants(presentation + "Button").Where(button =>
            (string?)button.Attribute("CommandParameter") == "Back" &&
            ((string?)button.Attribute("Command"))?.Contains("KeypadInputCommand", StringComparison.Ordinal) == true));
        Assert.Equal("44", Metric((string?)backspaceButton.Attribute("Height")));

        var inputBorder = Assert.Single(view.Descendants(presentation + "Border").Where(border =>
            border.Descendants(presentation + "TextBlock").Any(text =>
                ((string?)text.Attribute("Text"))?.Contains("pos.terminal.inputBuffer", StringComparison.Ordinal) == true)));
        Assert.Equal("78", Metric((string?)inputBorder.Attribute("Height")));
        Assert.Equal("12,6", (string?)inputBorder.Attribute("Padding"));
        Assert.Equal("13,10,13,8", (string?)inputBorder.Attribute("Margin"));
        Assert.Equal("6", (string?)inputBorder.Attribute("CornerRadius"));
        var inputLabel = Assert.Single(inputBorder.Descendants(presentation + "TextBlock").Where(text =>
            ((string?)text.Attribute("Text"))?.Contains("pos.terminal.inputBuffer", StringComparison.Ordinal) == true));
        Assert.Equal("11", Metric((string?)inputLabel.Attribute("FontSize")));
        var inputValue = Assert.Single(inputBorder.Descendants(presentation + "TextBlock").Where(text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding KeypadBuffer", StringComparison.Ordinal) == true));
        Assert.Equal("34", Metric((string?)inputValue.Attribute("FontSize")));
        Assert.Equal("Black", (string?)inputValue.Attribute("FontWeight"));

        // 键盘区 = 外层 5 等分行的 Grid：数字键 UniformGrid（3×4，跨 4 行）+ 底部 $0.50 / $0.99 各占半行（第 5 行）。
        // 整单开关已搬到下方操作区，键盘里不再有 ToggleButton；外层 Margin 与原来的 3×5 键盘一致，每键高度不变。
        var keypad = Assert.Single(view.Descendants(presentation + "UniformGrid").Where(grid =>
            (string?)grid.Attribute(x + "Name") == "CashierKeypad"));
        var keypadHost = Assert.IsType<XElement>(keypad.Parent);
        Assert.Equal(presentation + "Grid", keypadHost.Name);
        Assert.Equal("11,0,11,6", (string?)keypadHost.Attribute("Margin"));
        Assert.Equal(5, keypadHost.Element(presentation + "Grid.RowDefinitions")!.Elements().Count());
        Assert.Equal("0", (string?)keypad.Attribute("Grid.Row"));
        Assert.Equal("4", (string?)keypad.Attribute("Grid.RowSpan"));
        Assert.Equal(12, keypad.Elements(presentation + "Button").Count());
        Assert.Contains(keypad.Elements(presentation + "Button"), button =>
            (string?)button.Attribute("Style") == "{StaticResource CashierNumberKeyStyle}");
        Assert.Empty(keypad.Descendants(presentation + "ToggleButton"));

        var quickAmountKeys = Assert.Single(view.Descendants(presentation + "UniformGrid").Where(grid =>
            (string?)grid.Attribute(x + "Name") == "CashierQuickAmountKeys"));
        Assert.Same(keypadHost, quickAmountKeys.Parent);
        Assert.Equal("4", (string?)quickAmountKeys.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)quickAmountKeys.Attribute("Columns"));
        Assert.Equal(
            ["QuickHalf", "QuickNinetyNine"],
            quickAmountKeys.Elements(presentation + "Button").Select(button => (string?)button.Attribute("CommandParameter")));
        Assert.All(quickAmountKeys.Elements(presentation + "Button"), button =>
            Assert.Equal("{StaticResource CashierQuickKeyStyle}", (string?)button.Attribute("Style")));

        var flatButtonBase = FindStyle(view, x, "CashierFlatButtonBaseStyle");
        // 键面 Root 之下多了一层「键帽厚度」KeyShadow（旧主题透明、PosHomeKeyDepth 为 0，视觉不变）。
        var flatButtonBorder = Assert.Single(flatButtonBase.Descendants(presentation + "Border").Where(border =>
            (string?)border.Attribute(x + "Name") == "Root"));
        Assert.Equal("5", (string?)flatButtonBorder.Attribute("CornerRadius"));
        Assert.Equal("0", Metric((string?)flatButtonBorder.Attribute("Margin")));
        var keyShadow = Assert.Single(flatButtonBase.Descendants(presentation + "Border").Where(border =>
            (string?)border.Attribute(x + "Name") == "KeyShadow"));
        Assert.Equal("{DynamicResource PosKeyShadowBrush}", (string?)keyShadow.Attribute("Background"));
        Assert.Empty(flatButtonBase.Descendants(presentation + "DropShadowEffect"));
        var numberStyle = FindStyle(view, x, "CashierNumberKeyStyle");
        AssertSetter(numberStyle, "MinHeight", "44");
        AssertSetter(numberStyle, "Margin", "2");
        AssertMetricSetter(numberStyle, "FontSize", "28");
        AssertSetter(numberStyle, "FontWeight", "Black");
        var clearStyle = FindStyle(view, x, "CashierClearKeyStyle");
        AssertMetricSetter(clearStyle, "FontSize", "18");
        AssertSetter(clearStyle, "FontWeight", "Medium");
        var quickStyle = FindStyle(view, x, "CashierQuickKeyStyle");
        AssertMetricSetter(quickStyle, "FontSize", "18");
        AssertSetter(quickStyle, "FontWeight", "SemiBold");

        var middleActionGrid = Assert.Single(view.Descendants(presentation + "Grid").Where(
            element => (string?)element.Attribute("Grid.Row") == "3" &&
                           element.Descendants(presentation + "Button").Any(button =>
                               ((string?)button.Attribute("Command"))?.Contains("ModifySelectedLineQuantityCommand", StringComparison.Ordinal) == true)));
        // 操作区外边距 12 = 原来 Grid 外边距 9 + 按钮外边距 3；底部 8 与改版前的功能区总占高（190）持平。
        Assert.Equal("12,0,12,8", (string?)middleActionGrid.Attribute("Margin"));
        Assert.Equal(
            ["52", "6", "52", "6", "Auto"],
            middleActionGrid.Element(presentation + "Grid.RowDefinitions")!.Elements().Select(row => Metric((string?)row.Attribute("Height"))));
        Assert.Equal(
            ["76", "6", "*", "6", "*"],
            middleActionGrid.Element(presentation + "Grid.ColumnDefinitions")!.Elements().Select(column => (string?)column.Attribute("Width")));

        // 整单开关是一次性的「就绪」开关：与折扣金额 / 折扣% 同在中行，开关在最左，直接绑定视图模型的 IsWholeOrderOperation。
        var wholeOrderToggle = Assert.Single(middleActionGrid.Elements(presentation + "ToggleButton"));
        Assert.Equal("2", (string?)wholeOrderToggle.Attribute("Grid.Row"));
        Assert.Equal("0", (string?)wholeOrderToggle.Attribute("Grid.Column"));
        Assert.Equal("{Binding IsWholeOrderOperation, Mode=TwoWay}", (string?)wholeOrderToggle.Attribute("IsChecked"));
        Assert.Equal("{StaticResource CashierWholeOrderToggleStyle}", (string?)wholeOrderToggle.Attribute("Style"));

        var wholeOrderStyle = FindStyle(view, x, "CashierWholeOrderToggleStyle");
        var checkedTrigger = Assert.Single(wholeOrderStyle.Descendants(presentation + "Trigger").Where(trigger =>
            (string?)trigger.Attribute("Property") == "IsChecked" && (string?)trigger.Attribute("Value") == "True"));
        AssertTriggerSetter(checkedTrigger, "Background", "{DynamicResource PosPrimarySoftBrush}");
        AssertTriggerSetter(checkedTrigger, "Foreground", "{DynamicResource PosPrimaryTextBrush}");
        // 就绪态用虚线框 + 箭头表示（不只靠颜色）：实线边框让位，虚线框和箭头显示，且都取主题主色。
        AssertTriggerSetter(checkedTrigger, "BorderThickness", "0");
        Assert.Contains(checkedTrigger.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "ArmedFrame" &&
            (string?)setter.Attribute("Property") == "Visibility" &&
            (string?)setter.Attribute("Value") == "Visible");
        Assert.Contains(checkedTrigger.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "ArmedArrow" &&
            (string?)setter.Attribute("Property") == "Visibility" &&
            (string?)setter.Attribute("Value") == "Visible");
        var armedFrame = Assert.Single(wholeOrderStyle.Descendants(presentation + "Rectangle"));
        Assert.Equal("ArmedFrame", (string?)armedFrame.Attribute(x + "Name"));
        Assert.Equal("{DynamicResource PosPrimaryBrush}", (string?)armedFrame.Attribute("Stroke"));
        Assert.Equal("Collapsed", (string?)armedFrame.Attribute("Visibility"));
        var armedArrow = Assert.Single(wholeOrderStyle.Descendants(presentation + "Polygon"));
        Assert.Equal("ArmedArrow", (string?)armedArrow.Attribute(x + "Name"));
        Assert.Equal("{DynamicResource PosPrimaryBrush}", (string?)armedArrow.Attribute("Fill"));
        Assert.Equal("Collapsed", (string?)armedArrow.Attribute("Visibility"));

        // 折扣两键与快速折扣条随开关描蓝：DataTrigger 绑 IsWholeOrderOperation，颜色取主题画刷。
        var scopedStyle = FindStyle(view, x, "CashierScopedFunctionButtonStyle");
        Assert.Equal("{StaticResource CashierFunctionButtonStyle}", (string?)scopedStyle.Attribute("BasedOn"));
        var scopedTrigger = Assert.Single(scopedStyle.Descendants(presentation + "DataTrigger"));
        Assert.Equal("{Binding IsWholeOrderOperation}", (string?)scopedTrigger.Attribute("Binding"));
        Assert.Equal("True", (string?)scopedTrigger.Attribute("Value"));
        AssertTriggerSetter(scopedTrigger, "BorderBrush", "{DynamicResource PosPrimaryBrush}");
        AssertTriggerSetter(scopedTrigger, "Background", "{DynamicResource PosPrimarySoftBrush}");

        var segmentStyle = FindStyle(view, x, "CashierQuickDiscountSegmentStyle");
        AssertSetter(segmentStyle, "Background", "{DynamicResource PosSurfaceBrush}");
        AssertSetter(segmentStyle, "BorderBrush", "{DynamicResource PosKeyBorderBrush}");
        var segmentTrigger = Assert.Single(segmentStyle.Descendants(presentation + "DataTrigger"));
        Assert.Equal("{Binding IsWholeOrderOperation}", (string?)segmentTrigger.Attribute("Binding"));
        AssertTriggerSetter(segmentTrigger, "BorderBrush", "{DynamicResource PosPrimaryBrush}");
        AssertTriggerSetter(segmentTrigger, "Background", "{DynamicResource PosPrimarySoftBrush}");

        var functionStyle = FindStyle(view, x, "CashierFunctionButtonStyle");
        AssertMetricSetter(functionStyle, "Height", "52");
        AssertSetter(functionStyle, "Margin", "0");

        var discountStyle = FindStyle(view, x, "CashierDiscountButtonStyle");
        AssertMetricSetter(discountStyle, "Height", "64");
        AssertSetter(discountStyle, "Margin", "0");
        AssertSetter(discountStyle, "Background", "Transparent");
        AssertSetter(discountStyle, "BorderThickness", "0,0,1,0");

        var quickDiscountButtons = view.Descendants(presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("ApplyQuickDiscountPercentCommand", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(5, quickDiscountButtons.Length);
        Assert.Equal(["10", "20", "30", "40", "50"],
            quickDiscountButtons.Select(button => (string?)button.Attribute("CommandParameter")));
        Assert.All(quickDiscountButtons, button =>
        {
            Assert.Null(button.Attribute("Background"));
            Assert.Null(button.Attribute("Foreground"));
            Assert.Null(button.Attribute("IsChecked"));
        });
        Assert.Equal("0", (string?)quickDiscountButtons[^1].Attribute("BorderThickness"));

        var discountSegment = Assert.Single(view.Descendants(presentation + "Border").Where(border =>
            (string?)border.Attribute(x + "Name") == "QuickDiscountSegment"));
        // 快速折扣条占操作区最下一行、横跨五列；底色 / 边框写在样式里（本地值会盖住 DataTrigger），所以元素上不再有 Background / BorderBrush。
        Assert.Equal("4", (string?)discountSegment.Attribute("Grid.Row"));
        Assert.Equal("5", (string?)discountSegment.Attribute("Grid.ColumnSpan"));
        Assert.Null(discountSegment.Attribute("Margin"));
        Assert.Equal("{StaticResource CashierQuickDiscountSegmentStyle}", (string?)discountSegment.Attribute("Style"));
        Assert.Null(discountSegment.Attribute("Background"));
        Assert.Null(discountSegment.Attribute("BorderBrush"));
        Assert.Equal("5", (string?)discountSegment.Attribute("CornerRadius"));
        Assert.Null(discountSegment.Attribute("ClipToBounds"));
        Assert.Equal("1", (string?)discountSegment.Attribute("BorderThickness"));
        var opacityMask = Assert.Single(discountSegment.Elements(presentation + "Border.OpacityMask"));
        var visualBrush = Assert.Single(opacityMask.Elements(presentation + "VisualBrush"));
        var maskVisual = Assert.Single(visualBrush.Elements(presentation + "VisualBrush.Visual"));
        var maskBorder = Assert.Single(maskVisual.Elements(presentation + "Border"));
        Assert.Equal("5", (string?)maskBorder.Attribute("CornerRadius"));
        Assert.Equal("Black", (string?)maskBorder.Attribute("Background"));

        var noBarcodeButton = Assert.Single(view.Descendants(presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("AddOpenItemCommand", StringComparison.Ordinal) == true));
        Assert.Equal("13,0,13,8", (string?)noBarcodeButton.Attribute("Margin"));
        const string ancestorForeground = "{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}";
        Assert.Empty(noBarcodeButton.Descendants().Where(element => element.Name.LocalName == "PackIcon"));
        Assert.Equal(ancestorForeground, (string?)Assert.Single(noBarcodeButton.Descendants(presentation + "TextBlock")).Attribute("Foreground"));
        Assert.Equal("{StaticResource CashierNoBarcodeButtonStyle}", (string?)noBarcodeButton.Attribute("Style"));
        var noBarcodeStyle = FindStyle(view, x, "CashierNoBarcodeButtonStyle");
        // 配色全部走 PosNoBarcode* 键：旧主题取值＝原来的主色实心 + 白字，长者主题＝浅蓝底深蓝字。
        AssertSetter(noBarcodeStyle, "Background", "{DynamicResource PosNoBarcodeBrush}");
        AssertSetter(noBarcodeStyle, "BorderBrush", "{DynamicResource PosNoBarcodeBrush}");
        AssertSetter(noBarcodeStyle, "Foreground", "{DynamicResource PosNoBarcodeForegroundBrush}");

        // 修改数量 / 价格只管选中行，用普通功能键样式；折扣金额 / 折扣% 受整单开关管辖，用会随开关描蓝的样式。
        var functionCommands = new[]
        {
            ("ModifySelectedLineQuantityCommand", "{StaticResource CashierFunctionButtonStyle}"),
            ("ModifySelectedLinePriceCommand", "{StaticResource CashierFunctionButtonStyle}"),
            ("ApplySelectedLineDiscountAmountCommand", "{StaticResource CashierScopedFunctionButtonStyle}"),
            ("ApplySelectedLineDiscountPercentCommand", "{StaticResource CashierScopedFunctionButtonStyle}")
        };
        Assert.All(functionCommands, expected =>
        {
            var button = Assert.Single(view.Descendants(presentation + "Button").Where(element =>
                ((string?)element.Attribute("Command"))?.Contains(expected.Item1, StringComparison.Ordinal) == true));
            Assert.Equal(expected.Item2, (string?)button.Attribute("Style"));
            Assert.Empty(button.Descendants().Where(element => element.Name.LocalName == "PackIcon"));
        });
    }

    [Fact]
    public void Cart_scrollbar_reserves_width_without_showing_inactive_chrome()
    {
        var repoRoot = FindRepoRoot();
        var view = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PosTerminalView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var cartGrid = Assert.Single(view.Descendants(presentation + "DataGrid").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "CartItemsGrid"));
        Assert.Equal("Visible", (string?)cartGrid.Attribute("ScrollViewer.VerticalScrollBarVisibility"));

        var scrollBarStyle = FindStyle(view, x, "CartTouchScrollBarStyle");
        AssertSetter(scrollBarStyle, "Width", "22");
        AssertSetter(scrollBarStyle, "MinWidth", "22");
        var inactiveTrigger = Assert.Single(scrollBarStyle.Descendants(presentation + "Trigger").Where(trigger =>
            (string?)trigger.Attribute("Property") == "Maximum" && (string?)trigger.Attribute("Value") == "0"));
        AssertTriggerSetter(inactiveTrigger, "Opacity", "0");
    }

    [Fact]
    public void Scan_match_candidate_cards_show_thumbnail_and_item_number()
    {
        var repoRoot = FindRepoRoot();
        var view = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PosTerminalView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var matchesPopup = Assert.Single(view.Descendants(presentation + "Popup").Where(popup =>
            (string?)popup.Attribute(x + "Name") == "MatchesPopup"));
        var card = Assert.Single(matchesPopup.Descendants(presentation + "Button").Where(button =>
            (string?)button.Attribute(x + "Name") == "MatchCandidateCard"));
        Assert.Contains("SelectMatchCommand", (string?)card.Attribute("Command"));

        // 码冲突时各候选的码相同，必须靠商品图与货号区分；图片走异步缩略图路径，不能同步加载阻塞扫码。
        var thumbnail = Assert.Single(card.Descendants(presentation + "ImageBrush"));
        Assert.Contains(thumbnail.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".AsyncSourceText", StringComparison.Ordinal) &&
            attribute.Value == "{Binding ProductImage}");
        Assert.Null(thumbnail.Attribute("ImageSource"));
        Assert.Contains(card.Descendants(presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{loc:Loc ItemNumber}");
        var itemNumberValue = Assert.Single(card.Descendants(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "{Binding ItemNumber}"));
        Assert.Equal(
            "{Binding ItemNumber, Converter={StaticResource StringHasValueToVis}}",
            (string?)itemNumberValue.Attribute("Visibility"));
        Assert.Contains(card.Descendants(presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "-" &&
            (string?)text.Attribute("Visibility") == "{Binding ItemNumber, Converter={StaticResource StringIsEmptyToVis}}");
        Assert.Contains(card.Descendants(presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding LookupCode}");
        Assert.Contains(card.Descendants(presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding RetailPrice, StringFormat={}{0:C2}}");
    }

    [Fact]
    public void Pos_terminal_prioritizes_cart_and_preserves_compact_sidebar_actions()
    {
        var repoRoot = FindRepoRoot();
        var view = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PosTerminalView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var rootColumns = Assert.Single(view.Root!.Elements(presentation + "Grid"))
            .Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .ToArray();
        Assert.Equal(["58*", "26*", "16*"], rootColumns.Select(column => (string?)column.Attribute("Width")));
        Assert.Equal(["600", "280", "180"], rootColumns.Select(column => (string?)column.Attribute("MinWidth")));

        var searchHost = Assert.Single(view.Descendants(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "SearchBoxHost"));
        Assert.Equal("{DynamicResource PosSurfaceBrush}", (string?)searchHost.Attribute("Background"));
        Assert.Equal("{DynamicResource PosKeyBorderBrush}", (string?)searchHost.Attribute("BorderBrush"));
        Assert.Equal("1", (string?)searchHost.Attribute("BorderThickness"));

        var itemColumn = Assert.Single(view.Descendants(presentation + "DataGridTemplateColumn").Where(column =>
            (string?)column.Attribute("Header") == "{loc:Loc Item}"));
        Assert.Equal("2.6*", (string?)itemColumn.Attribute("Width"));
        var itemImageBrush = Assert.Single(itemColumn.Descendants(presentation + "ImageBrush"));
        Assert.Contains(itemImageBrush.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".AsyncSourceText", StringComparison.Ordinal) &&
            attribute.Value == "{Binding ProductImage}");
        Assert.Contains(itemColumn.Descendants(presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{loc:Loc ItemNumber}");
        Assert.DoesNotContain(itemColumn.Descendants(presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{loc:Loc Barcode}");
        var itemMetadataLine = Assert.Single(itemColumn.Descendants(presentation + "StackPanel").Where(panel =>
            (string?)panel.Attribute(x + "Name") == "CartItemMetadataLine"));
        Assert.Equal("Horizontal", (string?)itemMetadataLine.Attribute("Orientation"));
        var itemNumberValue = Assert.Single(itemColumn.Descendants(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "{Binding ItemNumber}" &&
            Metric((string?)text.Attribute("FontSize")) == "11"));
        Assert.Equal(
            "{Binding ItemNumber, Converter={StaticResource StringHasValueToVis}}",
            (string?)itemNumberValue.Attribute("Visibility"));
        Assert.Equal("NoWrap", (string?)itemNumberValue.Attribute("TextWrapping"));
        Assert.Null(itemNumberValue.Attribute("TextTrimming"));
        var barcodeValue = Assert.Single(itemColumn.Descendants(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "{Binding LookupCode}" &&
            Metric((string?)text.Attribute("FontSize")) == "11"));
        Assert.Equal(
            "{Binding LookupCode, Converter={StaticResource StringHasValueToVis}}",
            (string?)barcodeValue.Attribute("Visibility"));
        Assert.Equal("NoWrap", (string?)barcodeValue.Attribute("TextWrapping"));
        Assert.Null(barcodeValue.Attribute("TextTrimming"));
        Assert.Same(itemMetadataLine, itemNumberValue.Ancestors(presentation + "StackPanel").First(panel =>
            (string?)panel.Attribute(x + "Name") is not null));
        Assert.Same(itemMetadataLine, barcodeValue.Ancestors(presentation + "StackPanel").First(panel =>
            (string?)panel.Attribute(x + "Name") is not null));
        var missingMetadataValues = itemColumn.Descendants(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "-" &&
            Metric((string?)text.Attribute("FontSize")) == "11").ToArray();
        Assert.Equal(2, missingMetadataValues.Length);
        Assert.Contains(missingMetadataValues, text =>
            (string?)text.Attribute("Visibility") ==
            "{Binding ItemNumber, Converter={StaticResource StringIsEmptyToVis}}");
        Assert.Contains(missingMetadataValues, text =>
            (string?)text.Attribute("Visibility") ==
            "{Binding LookupCode, Converter={StaticResource StringIsEmptyToVis}}");
        var cartGrid = Assert.Single(view.Descendants(presentation + "DataGrid").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "CartItemsGrid"));
        Assert.Null(cartGrid.Attribute("RowHeight"));
        Assert.Equal("67", Metric((string?)cartGrid.Attribute("MinRowHeight")));
        Assert.Equal("36", Metric((string?)cartGrid.Attribute("ColumnHeaderHeight")));
        Assert.Equal("1000000", (string?)cartGrid.Attribute("AlternationCount"));
        var cartColumns = Assert.Single(cartGrid.Elements(presentation + "DataGrid.Columns"))
            .Elements()
            .ToArray();
        Assert.Equal(5, cartColumns.Length);
        var rowNumberColumn = cartColumns[0];
        Assert.Equal("#", (string?)rowNumberColumn.Attribute("Header"));
        Assert.Equal("44", (string?)rowNumberColumn.Attribute("Width"));
        Assert.Equal(
            "{StaticResource CartColumnHeaderCenterStyle}",
            (string?)rowNumberColumn.Attribute("HeaderStyle"));
        var rowNumberText = Assert.Single(rowNumberColumn.Descendants(presentation + "TextBlock"));
        Assert.Equal(
            "{Binding RelativeSource={RelativeSource AncestorType=DataGridRow}, Path=(ItemsControl.AlternationIndex), Converter={StaticResource RowNumberConverter}}",
            (string?)rowNumberText.Attribute("Text"));
        Assert.Equal("13", Metric((string?)rowNumberText.Attribute("FontSize")));
        Assert.Equal("SemiBold", (string?)rowNumberText.Attribute("FontWeight"));
        Assert.Equal("Center", (string?)rowNumberText.Attribute("TextAlignment"));
        Assert.Equal("Stretch", (string?)rowNumberText.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)rowNumberText.Attribute("VerticalAlignment"));
        Assert.Equal("{loc:Loc Item}", (string?)cartColumns[1].Attribute("Header"));
        Assert.Equal("{loc:Loc Quantity}", (string?)cartColumns[3].Attribute("Header"));
        Assert.Equal("122", (string?)cartColumns[3].Attribute("MinWidth"));
        var finalAmount = Assert.Single(view.Descendants(presentation + "TextBlock").Where(text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding ActualAmount", StringComparison.Ordinal) == true &&
            Metric((string?)text.Attribute("FontSize")) == "24"));
        // 应付合计金额色改走 PosDueAmountBrush：旧主题取值＝PosAccentBrush，长者主题是近黑色。
        Assert.Equal("{DynamicResource PosDueAmountBrush}", (string?)finalAmount.Attribute("Foreground"));
        var summaryPanel = Assert.Single(view.Descendants(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "CartSummaryPanel"));
        var summaryRows = Assert.Single(summaryPanel.Elements(presentation + "Grid"))
            .Element(presentation + "Grid.RowDefinitions")!
            .Elements(presentation + "RowDefinition")
            .ToArray();
        Assert.Equal(["34", "58"], summaryRows.Select(row => Metric((string?)row.Attribute("Height"))));
        var countSummaryRow = Assert.Single(view.Descendants(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "CartCountSummaryRow"));
        Assert.Equal("0", (string?)countSummaryRow.Attribute("Grid.Row"));
        var countSummaryTexts = countSummaryRow.Descendants(presentation + "TextBlock").ToArray();
        Assert.Contains(countSummaryTexts, text =>
            (string?)text.Attribute("Text") == "{Binding CartItemQuantity, StringFormat={}{0:0.##}}");
        Assert.Contains(countSummaryTexts, text =>
            (string?)text.Attribute("Text") == "{Binding CartSkuCount}");
        var countSummaryGrid = Assert.Single(countSummaryRow.Elements(presentation + "Grid"));
        var countSummaryColumns = countSummaryGrid.Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .ToArray();
        Assert.Equal(["Auto", "Auto", "Auto", "Auto", "*"], countSummaryColumns.Select(column =>
            (string?)column.Attribute("Width")));
        var skuLabel = Assert.Single(countSummaryTexts.Where(text =>
            (string?)text.Attribute("Text") == "{loc:Loc pos.terminal.cart.skuCount}"));
        var skuValue = Assert.Single(countSummaryTexts.Where(text =>
            (string?)text.Attribute("Text") == "{Binding CartSkuCount}"));
        Assert.Equal("2", (string?)skuLabel.Attribute("Grid.Column"));
        Assert.Equal("3", (string?)skuValue.Attribute("Grid.Column"));
        var totalsSummaryRow = Assert.Single(view.Descendants(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "CartTotalsSummaryRow"));
        Assert.Equal("1", (string?)totalsSummaryRow.Attribute("Grid.Row"));
        var totalsGrid = Assert.Single(totalsSummaryRow.Elements(presentation + "Grid"));
        var totalsColumns = totalsGrid.Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .ToArray();
        Assert.Equal(["0.9*", "0.9*", "1.2*"], totalsColumns.Select(column => (string?)column.Attribute("Width")));
        var totalsTexts = totalsSummaryRow.Descendants(presentation + "TextBlock").ToArray();
        Assert.Contains(totalsTexts, text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding TotalAmount", StringComparison.Ordinal) == true);
        Assert.Contains(totalsTexts, text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding DiscountAmount", StringComparison.Ordinal) == true);
        Assert.Contains(totalsTexts, text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding ActualAmount", StringComparison.Ordinal) == true);

        var sidebar = Assert.Single(view.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "AttendanceQrSidebar"));
        var actionGrid = Assert.Single(sidebar.Elements(presentation + "UniformGrid"));
        Assert.Equal("8,13,8,6", (string?)actionGrid.Attribute("Margin"));
        var actionButtons = actionGrid.Elements(presentation + "Button").ToArray();
        Assert.Equal(
            [
                "{Binding OpenSpecialProductsCommand}",
                "{Binding OpenReturnsCommand}",
                "{Binding HoldOrderCommand}",
                "{Binding RecallOrderCommand}",
                "{Binding OpenCashDrawerCommand}",
                "{Binding PrintLastReceiptCommand}",
                "{Binding LockCashierCommand}",
                "{Binding OpenDailyCloseCommand}",
                "{Binding OpenSettingsCommand}",
                "{Binding ExitApplicationCommand}",
            ],
            actionButtons.Select(button => (string?)button.Attribute("Command")));
        Assert.All(actionButtons[..^1], button =>
            Assert.Equal("{StaticResource PosSidebarActionButtonStyle}", (string?)button.Attribute("Style")));
        Assert.Equal("{StaticResource PosSidebarExitButtonStyle}", (string?)actionButtons[^1].Attribute("Style"));

        var actionStyle = FindStyle(view, x, "PosSidebarActionButtonStyle");
        AssertSetter(actionStyle, "MinHeight", "62");
        AssertSetter(actionStyle, "Margin", "3");
        AssertSetter(actionStyle, "BorderBrush", "{DynamicResource PosKeyBorderBrush}");
        var labelStyle = FindStyle(view, x, "PosSidebarActionLabelStyle");
        AssertSetter(labelStyle, "TextAlignment", "Center");
        AssertSetter(labelStyle, "TextWrapping", "Wrap");
        var iconHostStyle = FindStyle(view, x, "PosSidebarActionIconHostStyle");
        AssertSetter(iconHostStyle, "Background", "Transparent");
        Assert.Equal(10, actionGrid.Descendants(presentation + "TextBlock").Count(text =>
            (string?)text.Attribute("Style") == "{StaticResource PosSidebarActionLabelStyle}"));
    }

    [Fact]
    public void Card_recovery_center_entry_stays_visible_and_only_warns_for_open_attempts()
    {
        var repoRoot = FindRepoRoot();
        var view = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PosTerminalView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var sidebar = Assert.Single(view.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "AttendanceQrSidebar"));
        var statusCard = Assert.Single(sidebar.Elements(presentation + "ContentControl"));
        Assert.Equal("154", Metric((string?)statusCard.Attribute("Height")));
        Assert.Equal("154", Metric((string?)statusCard.Attribute("MaxHeight")));
        Assert.Null(statusCard.Element(presentation + "ContentControl.Style"));

        var statusLayout = Assert.Single(statusCard.Elements(presentation + "Grid").Where(grid =>
            (string?)grid.Attribute(x + "Name") == "PosTerminalStatusCardLayout"));
        Assert.Equal("True", (string?)statusLayout.Attribute("ClipToBounds"));
        var statusRows = statusLayout.Element(presentation + "Grid.RowDefinitions")!
            .Elements(presentation + "RowDefinition")
            .ToArray();
        Assert.Equal(["Auto", "*", "Auto"], statusRows.Select(row => (string?)row.Attribute("Height")));
        var statusMessage = Assert.Single(statusLayout.Elements(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute(x + "Name") == "PosTerminalStatusMessage"));
        Assert.Equal("1", (string?)statusMessage.Attribute("Grid.Row"));
        Assert.Equal("Wrap", (string?)statusMessage.Attribute("TextWrapping"));
        Assert.Equal("CharacterEllipsis", (string?)statusMessage.Attribute("TextTrimming"));

        var entry = Assert.Single(statusCard.Descendants(presentation + "Button").Where(button =>
            (string?)button.Attribute("Command") == "{Binding OpenCardRecoveryCenterCommand}"));
        Assert.Equal("CardRecoveryCenterEntryButton", (string?)entry.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("{Binding CardRecoveryCenterAutomationName}", (string?)entry.Attribute("AutomationProperties.Name"));
        Assert.Equal("2", (string?)entry.Attribute("Grid.Row"));
        Assert.Equal("44", (string?)entry.Attribute("MinHeight"));
        Assert.Null(entry.Attribute("Visibility"));

        var buttonStyle = Assert.Single(entry.Elements(presentation + "Button.Style"))
            .Element(presentation + "Style")!;
        AssertSetter(buttonStyle, "Background", "{DynamicResource PosSurfaceBrush}");
        AssertSetter(buttonStyle, "BorderBrush", "{DynamicResource PosKeyBorderBrush}");
        AssertSetter(buttonStyle, "Foreground", "{DynamicResource PosTextBrush}");
        Assert.DoesNotContain(buttonStyle.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "FocusVisualStyle"
            && (string?)setter.Attribute("Value") == "{x:Null}");
        var buttonTemplate = Assert.Single(buttonStyle.Descendants(presentation + "ControlTemplate"));
        var keyboardFocusTrigger = Assert.Single(buttonTemplate.Descendants(presentation + "Trigger").Where(trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocused"
            && (string?)trigger.Attribute("Value") == "True"));
        AssertTriggerSetter(keyboardFocusTrigger, "BorderBrush", "{DynamicResource PosPrimaryBrush}");
        AssertTriggerSetter(keyboardFocusTrigger, "BorderThickness", "2");
        var warningTrigger = Assert.Single(
            buttonStyle.Element(presentation + "Style.Triggers")!
                .Elements(presentation + "DataTrigger")
                .Where(trigger =>
                    (string?)trigger.Attribute("Binding") == "{Binding HasOpenCardRecoveryAttempts}"
                    && (string?)trigger.Attribute("Value") == "True"));
        AssertTriggerSetter(warningTrigger, "Background", "{DynamicResource PosWarningSoftBrush}");
        AssertTriggerSetter(warningTrigger, "BorderBrush", "{DynamicResource PosWarningBrush}");
        AssertTriggerSetter(warningTrigger, "Foreground", "{DynamicResource PosWarningTextBrush}");

        var title = Assert.Single(entry.Descendants(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute(x + "Name") == "CardRecoveryCenterTitle"));
        Assert.Equal("{Binding CardRecoveryCenterText}", (string?)title.Attribute("Text"));

        var leadingIcon = Assert.Single(entry.Descendants().Where(element =>
            element.Name.LocalName == "PackIcon"
            && (string?)element.Attribute(x + "Name") == "CardRecoveryCenterStateIcon"));
        var leadingIconStyle = Assert.Single(leadingIcon.Elements().Where(element =>
            element.Name.LocalName == "PackIcon.Style")).Element(presentation + "Style")!;
        AssertSetter(leadingIconStyle, "Kind", "CreditCardSearchOutline");
        AssertSetter(leadingIconStyle, "Foreground", "{DynamicResource PosMutedForegroundBrush}");
        var warningIconTrigger = Assert.Single(leadingIconStyle.Descendants(presentation + "DataTrigger").Where(trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding HasOpenCardRecoveryAttempts}"
            && (string?)trigger.Attribute("Value") == "True"));
        AssertTriggerSetter(warningIconTrigger, "Kind", "AlertCircleOutline");
        AssertTriggerSetter(warningIconTrigger, "Foreground", "{DynamicResource PosWarningTextBrush}");

        var badge = Assert.Single(entry.Descendants(presentation + "Border").Where(border =>
            (string?)border.Attribute(x + "Name") == "CardRecoveryOpenCountBadge"));
        var badgeStyle = Assert.Single(badge.Elements(presentation + "Border.Style"))
            .Element(presentation + "Style")!;
        AssertSetter(badgeStyle, "Visibility", "Collapsed");
        var badgeTrigger = Assert.Single(badgeStyle.Descendants(presentation + "DataTrigger").Where(trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding HasOpenCardRecoveryAttempts}"
            && (string?)trigger.Attribute("Value") == "True"));
        AssertTriggerSetter(badgeTrigger, "Visibility", "Visible");
        AssertTriggerSetter(badgeTrigger, "Background", "{DynamicResource PosWarningStrongBrush}");
        var badgeText = Assert.Single(badge.Descendants(presentation + "TextBlock"));
        Assert.Equal("{Binding CardRecoveryOpenCount}", (string?)badgeText.Attribute("Text"));
    }

    [Theory]
    [InlineData(960, 540)]
    [InlineData(1024, 720)]
    [InlineData(1024, 728)]
    [InlineData(1080, 720)]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    public void Attendance_sidebar_keeps_five_action_rows_touchable_at_supported_sizes(int width, int height)
    {
        var repoRoot = FindRepoRoot();
        var mainWindow = XDocument.Load(Path.Combine(repoRoot, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "MainWindow.xaml"));
        Assert.True(width >= (double)mainWindow.Root!.Attribute("MinWidth")!);
        Assert.True(height >= (double)mainWindow.Root.Attribute("MinHeight")!);

        // 小屏整体等比缩小后，页面按缩放前的逻辑尺寸排版。
        var scale = AdaptiveUiScale.Calculate(width, height);
        var logicalWidth = width / scale;
        var logicalHeight = height / scale;

        var view = XDocument.Load(Path.Combine(repoRoot, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Screens", "PosTerminalView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var rootColumns = Assert.Single(view.Root!.Elements(presentation + "Grid"))
            .Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .ToArray();
        Assert.Equal(3, rootColumns.Length);
        Assert.True(rootColumns.Sum(column => (double)column.Attribute("MinWidth")!) <= logicalWidth);

        var actionStyle = FindStyle(view, x, "PosSidebarActionButtonStyle");
        AssertSetter(actionStyle, "MinHeight", "62");
        var actionButtonMargin = ParseVerticalMargin((string?)Assert.Single(actionStyle.Elements().Where(element =>
            element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Margin")).Attribute("Value"));

        var sidebar = Assert.Single(view.Descendants().Where(element => (string?)element.Attribute(x + "Name") == "AttendanceQrSidebar"));
        var actionGrid = Assert.Single(sidebar.Elements(presentation + "UniformGrid"));
        Assert.Equal(10, actionGrid.Elements(presentation + "Button").Count());
        var status = Assert.Single(sidebar.Elements(presentation + "ContentControl"));
        var launcher = Assert.Single(sidebar.Elements(presentation + "Button").Where(element =>
            (string?)element.Attribute(x + "Name") == "AttendanceQrLauncher"));
        Assert.True(MetricDouble(launcher, "Height") <= 62);
        var requiredHeight = (5 * (62 + actionButtonMargin))
            + ParseVerticalMargin((string?)actionGrid.Attribute("Margin"))
            + MetricDouble(status, "MaxHeight")
            + ParseVerticalMargin((string?)status.Attribute("Margin"))
            + MetricDouble(launcher, "Height")
            + ParseVerticalMargin((string?)launcher.Attribute("Margin"));
        Assert.True(requiredHeight <= logicalHeight - 54 - 42);
    }

    private static double ParseVerticalMargin(string? value)
    {
        var parts = value!.Split(',').Select(double.Parse).ToArray();
        return parts.Length switch
        {
            1 => parts[0] * 2,
            2 => parts[1] * 2,
            4 => parts[1] + parts[3],
            _ => throw new FormatException("无效的 Thickness。"),
        };
    }

    [Fact]
    public void Cart_rows_reveal_delete_action_without_a_fixed_delete_column()
    {
        var repoRoot = FindRepoRoot();
        var view = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PosTerminalView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace services = "clr-namespace:Hbpos.Client.Wpf.Services";

        var cartGrid = Assert.Single(view.Descendants(presentation + "DataGrid").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "CartItemsGrid"));
        Assert.Equal("True", (string?)cartGrid.Attribute(services + "CartSwipeRevealBehavior.IsEnabled"));
        Assert.Equal("VerticalFirst", (string?)cartGrid.Attribute("ScrollViewer.PanningMode"));

        var cartColumns = Assert.Single(cartGrid.Elements(presentation + "DataGrid.Columns"))
            .Elements()
            .ToArray();
        Assert.Equal(5, cartColumns.Length);
        Assert.DoesNotContain(cartColumns, column => column.Descendants(presentation + "Button").Any(button =>
            ((string?)button.Attribute("Command"))?.Contains("RemoveLineCommand", StringComparison.Ordinal) == true));

        var rowStyle = FindStyle(view, x, "CartDataGridRowStyle");
        var rowTemplate = Assert.Single(rowStyle.Descendants(presentation + "ControlTemplate"));
        var swipeContent = Assert.Single(rowTemplate.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "PART_SwipeContent"));
        Assert.Equal("True", (string?)swipeContent.Attribute("ClipToBounds"));
        Assert.Single(swipeContent.Descendants(presentation + "DataGridCellsPresenter"));

        var deleteButton = Assert.Single(rowTemplate.Descendants(presentation + "Button").Where(button =>
            (string?)button.Attribute(x + "Name") == "PART_SwipeDeleteAction"));
        Assert.Equal("88", (string?)deleteButton.Attribute("Width"));
        Assert.Equal("Right", (string?)deleteButton.Attribute("HorizontalAlignment"));
        Assert.Equal("Stretch", (string?)deleteButton.Attribute("VerticalAlignment"));
        Assert.Equal("{DynamicResource PosDangerBrush}", (string?)deleteButton.Attribute("Background"));
        Assert.Equal(
            "{Binding DataContext.RemoveLineCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}",
            (string?)deleteButton.Attribute("Command"));
        Assert.Equal("{Binding}", (string?)deleteButton.Attribute("CommandParameter"));
        Assert.Equal("Delete", (string?)deleteButton.Attribute(services + "ButtonFeedback.Cue"));
        Assert.Equal("{loc:Loc Remove}", (string?)deleteButton.Attribute("AutomationProperties.Name"));
        Assert.Contains(deleteButton.Descendants(), element =>
            element.Name.LocalName == "PackIcon" && (string?)element.Attribute("Kind") == "DeleteOutline");
        Assert.Contains(deleteButton.Descendants(presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{loc:Loc Remove}");
    }

    [Fact]
    public void Attendance_qr_uses_compact_launcher_and_native_overlay_dialog()
    {
        var repoRoot = FindRepoRoot();
        var view = XDocument.Load(Path.Combine(repoRoot, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Screens", "PosTerminalView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sidebar = Assert.Single(view.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "AttendanceQrSidebar"));
        var rows = Assert.Single(sidebar.Elements().Where(element => element.Name.LocalName == "Grid.RowDefinitions")).Elements().ToArray();
        Assert.Equal(["*", "Auto", "Auto"], rows.Select(row => (string?)row.Attribute("Height")));

        Assert.DoesNotContain(sidebar.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "AttendanceQrCard");
        var launcher = Assert.Single(sidebar.Elements().Where(element =>
            (string?)element.Attribute(x + "Name") == "AttendanceQrLauncher"));
        Assert.Equal("62", Metric((string?)launcher.Attribute("Height")));
        Assert.Equal("AttendanceQrLauncher_Click", (string?)launcher.Attribute("Click"));

        var overlay = Assert.Single(view.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "AttendanceQrOverlay"));
        Assert.Equal("0", (string?)overlay.Attribute("Grid.Column"));
        Assert.Equal("3", (string?)overlay.Attribute("Grid.ColumnSpan"));
        Assert.Equal("AttendanceQrOverlay_Click", (string?)overlay.Attribute("MouseLeftButtonDown"));
        Assert.Equal("{loc:Loc attendance.qr.title}", (string?)overlay.Attribute("AutomationProperties.Name"));
        Assert.Equal("{loc:Loc attendance.qr.scanHint}", (string?)overlay.Attribute("AutomationProperties.HelpText"));
        var dialog = Assert.Single(overlay.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "AttendanceQrDialog"));
        Assert.Equal("360", (string?)dialog.Attribute("Width"));
        Assert.Equal("430", (string?)dialog.Attribute("Height"));

        var image = Assert.Single(dialog.Descendants().Where(element =>
            element.Name.LocalName == "Image"
            && ((string?)element.Attribute("Source"))?.Contains("AttendanceQrPanel.QrImage", StringComparison.Ordinal) == true));
        Assert.Equal("260", (string?)image.Attribute("Width"));
        Assert.Equal("260", (string?)image.Attribute("Height"));
        Assert.Equal("None", (string?)image.Attribute("Stretch"));
        Assert.Equal("NearestNeighbor", (string?)image.Attribute("RenderOptions.BitmapScalingMode"));
        var closeButton = Assert.Single(dialog.Descendants().Where(element => element.Name.LocalName == "Button"
            && (string?)element.Attribute("Click") == "AttendanceQrCloseButton_Click"));
        Assert.Equal("{loc:Loc attendance.qr.closeHelp}", (string?)closeButton.Attribute("AutomationProperties.HelpText"));
        Assert.Equal("AttendanceQrView_KeyDown", (string?)view.Root!.Attribute("KeyDown"));

        var panelRunBindings = view.Descendants(presentation + "Run")
            .Select(run => (string?)run.Attribute("Text"))
            .Where(text => text?.StartsWith("{Binding AttendanceQrPanel.", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(
            [
                "{Binding AttendanceQrPanel.VerificationStatusText, Mode=OneWay}",
                "{Binding AttendanceQrPanel.MessageText, Mode=OneWay}",
                "{Binding AttendanceQrPanel.DeviceText, Mode=OneWay}",
            ],
            panelRunBindings);

        var english = XDocument.Load(Path.Combine(repoRoot, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Resources", "Strings.resx"));
        var chinese = XDocument.Load(Path.Combine(repoRoot, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Resources", "Strings.zh-CN.resx"));
        Assert.Contains(english.Descendants("data"), element => (string?)element.Attribute("name") == "attendance.qr.closeHelp");
        Assert.Contains(chinese.Descendants("data"), element => (string?)element.Attribute("name") == "attendance.qr.closeHelp");
    }

    // 首页的字号 / 行高 / 按钮高已改走 Themes/Metrics/Home.Standard.xaml 的 DynamicResource 键，
    // 标准值＝改版前的字面量。下面把 {DynamicResource PosHomeXxx} 解析回标准值，保持「老主题尺寸不变」的断言原意；
    // 色板画刷键（…Brush）不在尺寸字典里，原样返回。
    private static readonly Lazy<Dictionary<string, string>> StandardHomeMetrics = new(LoadStandardHomeMetrics);

    private static Dictionary<string, string> LoadStandardHomeMetrics()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Themes", "Metrics", "Home.Standard.xaml"));
        return document.Root!.Elements()
            .Where(element => element.Attribute(x + "Key") is not null)
            .ToDictionary(element => (string)element.Attribute(x + "Key")!, element => element.Value.Trim());
    }

    private static string? Metric(string? raw)
    {
        const string prefix = "{DynamicResource ";
        if (raw is null || !raw.StartsWith(prefix, StringComparison.Ordinal) || !raw.EndsWith('}'))
        {
            return raw;
        }

        var key = raw[prefix.Length..^1];
        return StandardHomeMetrics.Value.TryGetValue(key, out var value) ? value : raw;
    }

    private static double MetricDouble(XElement element, string attribute) =>
        double.Parse(Metric((string?)element.Attribute(attribute))!, CultureInfo.InvariantCulture);

    private static void AssertMetricSetter(XElement style, string property, string standardValue) =>
        Assert.Contains(style.Elements().Where(element => element.Name.LocalName == "Setter"), setter =>
            (string?)setter.Attribute("Property") == property && Metric((string?)setter.Attribute("Value")) == standardValue);

    private static XElement FindStyle(XDocument document, XNamespace x, string key) =>
        Assert.Single(document.Descendants().Where(element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute(x + "Key") == key));

    private static void AssertStyleSetter(XDocument document, XNamespace x, string key, string property, string value) =>
        AssertSetter(FindStyle(document, x, key), property, value);

    private static void AssertSetter(XElement style, string property, string value) =>
        Assert.Contains(style.Elements().Where(element => element.Name.LocalName == "Setter"), setter =>
            (string?)setter.Attribute("Property") == property && (string?)setter.Attribute("Value") == value);

    private static void AssertTriggerSetter(XElement trigger, string property, string value) =>
        Assert.Contains(trigger.Elements().Where(element => element.Name.LocalName == "Setter"), setter =>
            (string?)setter.Attribute("Property") == property && (string?)setter.Attribute("Value") == value);

    private static string FindRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        foreach (var start in new[]
                 {
                     Path.GetDirectoryName(sourceFilePath),
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory,
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
