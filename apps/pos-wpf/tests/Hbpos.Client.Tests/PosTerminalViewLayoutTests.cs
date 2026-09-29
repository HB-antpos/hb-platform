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
        Assert.Equal("44", (string?)backspaceButton.Attribute("Height"));

        var inputBorder = Assert.Single(view.Descendants(presentation + "Border").Where(border =>
            border.Descendants(presentation + "TextBlock").Any(text =>
                ((string?)text.Attribute("Text"))?.Contains("pos.terminal.inputBuffer", StringComparison.Ordinal) == true)));
        Assert.Equal("78", (string?)inputBorder.Attribute("Height"));
        Assert.Equal("12,6", (string?)inputBorder.Attribute("Padding"));
        Assert.Equal("13,10,13,8", (string?)inputBorder.Attribute("Margin"));
        Assert.Equal("6", (string?)inputBorder.Attribute("CornerRadius"));
        var inputLabel = Assert.Single(inputBorder.Descendants(presentation + "TextBlock").Where(text =>
            ((string?)text.Attribute("Text"))?.Contains("pos.terminal.inputBuffer", StringComparison.Ordinal) == true));
        Assert.Equal("11", (string?)inputLabel.Attribute("FontSize"));
        var inputValue = Assert.Single(inputBorder.Descendants(presentation + "TextBlock").Where(text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding KeypadBuffer", StringComparison.Ordinal) == true));
        Assert.Equal("34", (string?)inputValue.Attribute("FontSize"));
        Assert.Equal("Black", (string?)inputValue.Attribute("FontWeight"));

        var keypad = Assert.Single(view.Descendants(presentation + "UniformGrid").Where(grid =>
            grid.Descendants(presentation + "Button").Any(button =>
                (string?)button.Attribute("CommandParameter") == "QuickHalf")));
        Assert.Equal("11,0,11,6", (string?)keypad.Attribute("Margin"));
        Assert.Equal(14, keypad.Elements(presentation + "Button").Count());
        Assert.Contains(keypad.Elements(presentation + "Button"), button =>
            (string?)button.Attribute("Style") == "{StaticResource CashierNumberKeyStyle}");
        Assert.Equal(
            "{StaticResource CashierWholeOrderToggleStyle}",
            (string?)Assert.Single(keypad.Elements(presentation + "ToggleButton")).Attribute("Style"));

        var flatButtonBase = FindStyle(view, x, "CashierFlatButtonBaseStyle");
        var flatButtonBorder = Assert.Single(flatButtonBase.Descendants(presentation + "Border"));
        Assert.Equal("5", (string?)flatButtonBorder.Attribute("CornerRadius"));
        Assert.Empty(flatButtonBase.Descendants(presentation + "DropShadowEffect"));
        var numberStyle = FindStyle(view, x, "CashierNumberKeyStyle");
        AssertSetter(numberStyle, "MinHeight", "44");
        AssertSetter(numberStyle, "Margin", "2");
        AssertSetter(numberStyle, "FontSize", "28");
        AssertSetter(numberStyle, "FontWeight", "Black");
        var clearStyle = FindStyle(view, x, "CashierClearKeyStyle");
        AssertSetter(clearStyle, "FontSize", "18");
        AssertSetter(clearStyle, "FontWeight", "Medium");
        var quickStyle = FindStyle(view, x, "CashierQuickKeyStyle");
        AssertSetter(quickStyle, "FontSize", "18");
        AssertSetter(quickStyle, "FontWeight", "SemiBold");

        var middleActionGrid = Assert.Single(view.Descendants(presentation + "Grid").Where(
            element => (string?)element.Attribute("Grid.Row") == "3" &&
                           element.Descendants(presentation + "Button").Any(button =>
                               ((string?)button.Attribute("Command"))?.Contains("ModifySelectedLineQuantityCommand", StringComparison.Ordinal) == true)));
        Assert.Equal("9,0,9,0", (string?)middleActionGrid.Attribute("Margin"));

        var wholeOrderStyle = FindStyle(view, x, "CashierWholeOrderToggleStyle");
        var checkedTrigger = Assert.Single(wholeOrderStyle.Descendants(presentation + "Trigger").Where(trigger =>
            (string?)trigger.Attribute("Property") == "IsChecked" && (string?)trigger.Attribute("Value") == "True"));
        AssertTriggerSetter(checkedTrigger, "Background", "{DynamicResource PosPrimarySoftBrush}");
        AssertTriggerSetter(checkedTrigger, "BorderBrush", "{DynamicResource PosPrimaryBrush}");
        AssertTriggerSetter(checkedTrigger, "Foreground", "{DynamicResource PosPrimaryTextBrush}");

        var functionStyle = FindStyle(view, x, "CashierFunctionButtonStyle");
        AssertSetter(functionStyle, "Height", "54");
        AssertSetter(functionStyle, "Margin", "3");

        var discountStyle = FindStyle(view, x, "CashierDiscountButtonStyle");
        AssertSetter(discountStyle, "Height", "64");
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
        Assert.Equal("3,4,3,0", (string?)discountSegment.Attribute("Margin"));
        Assert.Equal("5", (string?)discountSegment.Attribute("CornerRadius"));
        Assert.Null(discountSegment.Attribute("ClipToBounds"));
        Assert.Equal("{DynamicResource PosBorderBrush}", (string?)discountSegment.Attribute("BorderBrush"));
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
        AssertSetter(noBarcodeStyle, "Background", "{DynamicResource PosPrimaryBrush}");
        AssertSetter(noBarcodeStyle, "BorderBrush", "{DynamicResource PosPrimaryBrush}");
        AssertSetter(noBarcodeStyle, "Foreground", "White");

        var functionCommands = new[]
        {
            "ModifySelectedLineQuantityCommand",
            "ModifySelectedLinePriceCommand",
            "ApplySelectedLineDiscountAmountCommand",
            "ApplySelectedLineDiscountPercentCommand"
        };
        Assert.All(functionCommands, command =>
        {
            var button = Assert.Single(view.Descendants(presentation + "Button").Where(element =>
                ((string?)element.Attribute("Command"))?.Contains(command, StringComparison.Ordinal) == true));
            Assert.Equal("{StaticResource CashierFunctionButtonStyle}", (string?)button.Attribute("Style"));
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
        Assert.Equal("{DynamicResource PosBorderBrush}", (string?)searchHost.Attribute("BorderBrush"));
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
            (string?)text.Attribute("FontSize") == "11"));
        Assert.Equal(
            "{Binding ItemNumber, Converter={StaticResource StringHasValueToVis}}",
            (string?)itemNumberValue.Attribute("Visibility"));
        Assert.Equal("NoWrap", (string?)itemNumberValue.Attribute("TextWrapping"));
        Assert.Null(itemNumberValue.Attribute("TextTrimming"));
        var barcodeValue = Assert.Single(itemColumn.Descendants(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "{Binding LookupCode}" &&
            (string?)text.Attribute("FontSize") == "11"));
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
            (string?)text.Attribute("FontSize") == "11").ToArray();
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
        Assert.Equal("67", (string?)cartGrid.Attribute("MinRowHeight"));
        Assert.Equal("36", (string?)cartGrid.Attribute("ColumnHeaderHeight"));
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
        Assert.Equal("13", (string?)rowNumberText.Attribute("FontSize"));
        Assert.Equal("SemiBold", (string?)rowNumberText.Attribute("FontWeight"));
        Assert.Equal("Center", (string?)rowNumberText.Attribute("TextAlignment"));
        Assert.Equal("Stretch", (string?)rowNumberText.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)rowNumberText.Attribute("VerticalAlignment"));
        Assert.Equal("{loc:Loc Item}", (string?)cartColumns[1].Attribute("Header"));
        Assert.Equal("{loc:Loc Quantity}", (string?)cartColumns[3].Attribute("Header"));
        Assert.Equal("122", (string?)cartColumns[3].Attribute("MinWidth"));
        var finalAmount = Assert.Single(view.Descendants(presentation + "TextBlock").Where(text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding ActualAmount", StringComparison.Ordinal) == true &&
            (string?)text.Attribute("FontSize") == "24"));
        Assert.Equal("{DynamicResource PosAccentBrush}", (string?)finalAmount.Attribute("Foreground"));
        var summaryPanel = Assert.Single(view.Descendants(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "CartSummaryPanel"));
        var summaryRows = Assert.Single(summaryPanel.Elements(presentation + "Grid"))
            .Element(presentation + "Grid.RowDefinitions")!
            .Elements(presentation + "RowDefinition")
            .ToArray();
        Assert.Equal(["34", "58"], summaryRows.Select(row => (string?)row.Attribute("Height")));
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
        AssertSetter(actionStyle, "BorderBrush", "{DynamicResource PosBorderBrush}");
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
        Assert.Equal("154", (string?)statusCard.Attribute("Height"));
        Assert.Equal("154", (string?)statusCard.Attribute("MaxHeight"));
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
        AssertSetter(buttonStyle, "BorderBrush", "{DynamicResource PosBorderBrush}");
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
        Assert.True((double)launcher.Attribute("Height")! <= 62);
        var requiredHeight = (5 * (62 + actionButtonMargin))
            + ParseVerticalMargin((string?)actionGrid.Attribute("Margin"))
            + (double)status.Attribute("MaxHeight")!
            + ParseVerticalMargin((string?)status.Attribute("Margin"))
            + (double)launcher.Attribute("Height")!
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
        Assert.Equal("62", (string?)launcher.Attribute("Height"));
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
