using Hbpos.Client.Wpf.ViewModels;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

public sealed class TransactionHistoryViewLayoutTests
{
    [Fact]
    public void History_return_rows_use_danger_highlight()
    {
        var isReturnOrder = typeof(HistoryOrderListItem).GetProperty(nameof(HistoryOrderListItem.IsReturnOrder));
        Assert.NotNull(isReturnOrder);
        Assert.False(isReturnOrder!.CanWrite);

        var view = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "TransactionHistoryView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var rowTrigger = Assert.Single(
            FindStyle(view, x, "HistoryRowStyle")
                .Descendants(presentation + "DataTrigger")
                .Where(element =>
                    (string?)element.Attribute("Binding") == "{Binding IsReturnOrder}" &&
                    (string?)element.Attribute("Value") == "True"));
        AssertSetter(rowTrigger, "Background", "{DynamicResource PosDangerSoftBrush}");
        AssertSetter(rowTrigger, "BorderBrush", "{DynamicResource PosDangerBrush}");

        var primaryTextTrigger = Assert.Single(
            FindStyle(view, x, "HistoryPrimaryTextStyle")
                .Descendants(presentation + "DataTrigger")
                .Where(element =>
                    (string?)element.Attribute("Binding") == "{Binding IsReturnOrder}" &&
                    (string?)element.Attribute("Value") == "True"));
        AssertSetter(primaryTextTrigger, "Foreground", "{DynamicResource PosDangerTextBrush}");
    }

    [Fact]
    public void History_order_id_run_uses_one_way_binding_for_read_only_property()
    {
        var displayOrderId = typeof(HistoryOrderListItem).GetProperty(nameof(HistoryOrderListItem.DisplayOrderId));
        Assert.NotNull(displayOrderId);
        Assert.False(displayOrderId!.CanWrite);

        var view = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "TransactionHistoryView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var orderIdRun = Assert.Single(view.Descendants(presentation + "Run").Where(element =>
            ((string?)element.Attribute("Text"))?.Contains("Binding DisplayOrderId", StringComparison.Ordinal) == true));

        Assert.Equal("{Binding DisplayOrderId, Mode=OneWay}", (string?)orderIdRun.Attribute("Text"));
    }

    [Fact]
    public void History_list_uses_two_line_summary_columns_and_receipt_action()
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
            "TransactionHistoryView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var historyGrid = Assert.Single(view.Descendants(presentation + "DataGrid").Where(element =>
            (string?)element.Attribute(x + "Name") == "HistoryOrdersGrid"));
        Assert.Equal("Disabled", (string?)historyGrid.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));

        var rowStyle = FindStyle(view, x, "HistoryRowStyle");
        AssertSetter(rowStyle, "Height", "72");
        AssertTextStyleDoesNotWrap(view, presentation, x, "HistoryPrimaryTextStyle");
        AssertTextStyleDoesNotWrap(view, presentation, x, "HistorySecondaryTextStyle");

        var columns = Assert.Single(historyGrid.Elements(presentation + "DataGrid.Columns"));
        Assert.Empty(columns.Elements(presentation + "DataGridTextColumn"));
        var columnNames = columns.Elements()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .ToArray();
        Assert.Contains("OrderSummaryColumn", columnNames);
        Assert.Contains("StandardCashierSummaryColumn", columnNames);
        Assert.Contains("InstallmentCustomerSummaryColumn", columnNames);
        Assert.Contains("StandardAmountSummaryColumn", columnNames);
        Assert.Contains("InstallmentAmountSummaryColumn", columnNames);
        Assert.Contains("StatusSummaryColumn", columnNames);
        Assert.Contains("HistoryActionsColumn", columnNames);

        var actionsColumn = Assert.Single(columns.Elements(presentation + "DataGridTemplateColumn").Where(element =>
            (string?)element.Attribute(x + "Name") == "HistoryActionsColumn"));
        Assert.Equal("268", (string?)actionsColumn.Attribute("Width"));

        var receiptButton = Assert.Single(actionsColumn.Descendants(presentation + "Button").Where(element =>
            ((string?)element.Attribute("Command"))?.Contains("OpenReceiptPreviewCommand", StringComparison.Ordinal) == true));
        Assert.Equal("{Binding}", (string?)receiptButton.Attribute("CommandParameter"));
        Assert.Equal("{loc:Loc history.viewReceipt}", (string?)receiptButton.Attribute("ToolTip"));
        Assert.Equal("{loc:Loc history.viewReceipt}", (string?)receiptButton.Attribute("AutomationProperties.Name"));
        Assert.Contains(receiptButton.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{loc:Loc history.viewReceipt}");

        var detailsButton = Assert.Single(actionsColumn.Descendants(presentation + "Button").Where(element =>
            ((string?)element.Attribute("Command"))?.Contains("OpenOrderDetailsCommand", StringComparison.Ordinal) == true));
        Assert.Equal("{Binding}", (string?)detailsButton.Attribute("CommandParameter"));
        Assert.Equal("{Binding CanViewOrderDetails}", (string?)detailsButton.Attribute("IsEnabled"));
        Assert.Equal("44", (string?)detailsButton.Attribute("Width"));
        Assert.Equal("44", (string?)detailsButton.Attribute("Height"));
        Assert.Equal("True", (string?)detailsButton.Attribute("ToolTipService.ShowOnDisabled"));
        Assert.Equal("{loc:Loc history.viewOrderDetails}", (string?)detailsButton.Attribute("AutomationProperties.Name"));
        Assert.Single(detailsButton.Descendants().Where(element =>
            element.Name.LocalName == "PackIcon" &&
            (string?)element.Attribute("Kind") == "FormatListBulleted"));
        var detailsDisabledHelpTrigger = Assert.Single(detailsButton.Descendants(presentation + "DataTrigger").Where(trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding CanViewOrderDetails}" &&
            (string?)trigger.Attribute("Value") == "False"));
        AssertSetter(
            detailsDisabledHelpTrigger,
            "AutomationProperties.HelpText",
            "{loc:Loc history.remoteHeldDetailsUnavailable}");
        var detailsDisabledToolTipTrigger = Assert.Single(detailsButton.Descendants(presentation + "DataTrigger").Where(trigger =>
            (string?)trigger.Attribute("Binding") ==
                "{Binding PlacementTarget.DataContext.CanViewOrderDetails, RelativeSource={RelativeSource Self}}" &&
            (string?)trigger.Attribute("Value") == "False"));
        AssertSetter(detailsDisabledToolTipTrigger, "Content", "{loc:Loc history.remoteHeldDetailsUnavailable}");

        var recallButton = Assert.Single(actionsColumn.Descendants(presentation + "Button").Where(element =>
            ((string?)element.Attribute("Command"))?.Contains("RecallOrderCommand", StringComparison.Ordinal) == true));
        Assert.Equal("44", (string?)recallButton.Attribute("Width"));
        Assert.Equal("44", (string?)recallButton.Attribute("Height"));
        Assert.Empty(recallButton.Descendants(presentation + "TextBlock"));

        var continuePaymentButton = Assert.Single(actionsColumn.Descendants(presentation + "Button").Where(element =>
            ((string?)element.Attribute("Command"))?.Contains("ContinueInstallmentPaymentCommand", StringComparison.Ordinal) == true));
        Assert.Equal("44", (string?)continuePaymentButton.Attribute("Width"));
        Assert.Equal("44", (string?)continuePaymentButton.Attribute("Height"));
        Assert.Empty(continuePaymentButton.Descendants(presentation + "TextBlock"));

        var moreButton = Assert.Single(actionsColumn.Descendants(presentation + "Button").Where(element =>
            (string?)element.Attribute("Click") == "OpenHistoryRowActionsMenu"));
        Assert.Equal("44", (string?)moreButton.Attribute("Width"));
        Assert.Equal("44", (string?)moreButton.Attribute("Height"));
        Assert.Equal("{loc:Loc history.moreActions}", (string?)moreButton.Attribute("ToolTip"));
        Assert.Equal("{loc:Loc history.moreActions}", (string?)moreButton.Attribute("AutomationProperties.Name"));

        var menu = Assert.Single(moreButton.Descendants(presentation + "ContextMenu"));
        Assert.Equal(
            "{Binding PlacementTarget, RelativeSource={RelativeSource Self}}",
            (string?)menu.Attribute("DataContext"));
        var menuItems = menu.Elements(presentation + "MenuItem").ToArray();
        Assert.Equal(3, menuItems.Length);
        Assert.All(menuItems, item => Assert.Equal("{Binding DataContext}", (string?)item.Attribute("CommandParameter")));
        Assert.Contains(menuItems, item =>
            ((string?)item.Attribute("Command"))?.Contains("ShareHeldOrderCommand", StringComparison.Ordinal) == true);
        Assert.Contains(menuItems, item =>
            ((string?)item.Attribute("Command"))?.Contains("DeleteHeldOrderCommand", StringComparison.Ordinal) == true);
        Assert.Contains(menuItems, item =>
            ((string?)item.Attribute("Command"))?.Contains("ForceReleaseHeldOrderCommand", StringComparison.Ordinal) == true);

        foreach (var key in new[]
                 {
                     "history.orderTime",
                     "history.cashierTerminal",
                     "history.customerPhone",
                     "history.amountItems",
                     "history.totalOutstanding",
                     "history.moreActions",
                     "history.viewReceipt",
                     "history.viewOrderDetails",
                     "history.remoteHeldDetailsUnavailable",
                 })
        {
            AssertLocalizationKey(repoRoot, "Strings.resx", key);
            AssertLocalizationKey(repoRoot, "Strings.zh-CN.resx", key);
        }
    }

    [Fact]
    public void History_receipt_preview_is_modal_and_table_uses_full_workspace_width()
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
            "TransactionHistoryView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var pageLayout = Assert.Single(view.Descendants(presentation + "Grid").Where(element =>
            (string?)element.Attribute(x + "Name") == "HistoryPageLayout"));
        Assert.Empty(pageLayout.Elements(presentation + "Grid.ColumnDefinitions"));

        var workspace = Assert.Single(pageLayout.Elements(presentation + "Grid").Where(element =>
            (string?)element.Attribute(x + "Name") == "HistoryWorkspace"));
        Assert.Null(workspace.Attribute("Grid.Column"));
        Assert.Equal("20,18", (string?)workspace.Attribute("Margin"));

        var overlay = Assert.Single(pageLayout.Elements(presentation + "Grid").Where(element =>
            (string?)element.Attribute(x + "Name") == "ReceiptPreviewOverlay"));
        Assert.Equal("#660F172A", (string?)overlay.Attribute("Background"));
        Assert.Equal("True", (string?)overlay.Attribute("FocusManager.IsFocusScope"));
        Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.Equal(
            "{Binding IsReceiptPreviewOpen, Converter={StaticResource BoolToVis}}",
            (string?)overlay.Attribute("Visibility"));

        var dialog = Assert.Single(overlay.Elements(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "ReceiptPreviewDialog"));
        Assert.Equal("680", (string?)dialog.Attribute("Width"));
        Assert.Equal("820", (string?)dialog.Attribute("MaxHeight"));

        var receiptRows = Assert.Single(dialog.Descendants(presentation + "ItemsControl").Where(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ReceiptPreviewRows}"));
        Assert.Equal("{StaticResource ReceiptPreviewRowTemplate}", (string?)receiptRows.Attribute("ItemTemplate"));

        Assert.Equal(2, dialog.Descendants(presentation + "Button").Count(element =>
            (string?)element.Attribute("Command") == "{Binding CloseReceiptPreviewCommand}"));
        Assert.Single(dialog.Descendants(presentation + "Button").Where(element =>
            (string?)element.Attribute("Command") == "{Binding ReprintCommand}"));

        var escapeBinding = Assert.Single(overlay.Descendants(presentation + "KeyBinding").Where(element =>
            (string?)element.Attribute("Key") == "Escape"));
        Assert.Equal("{Binding CloseReceiptPreviewCommand}", (string?)escapeBinding.Attribute("Command"));

        foreach (var key in new[]
                 {
                     "history.viewReceipt",
                     "history.closeReceiptPreview",
                     "history.receiptLoading",
                 })
        {
            AssertLocalizationKey(repoRoot, "Strings.resx", key);
            AssertLocalizationKey(repoRoot, "Strings.zh-CN.resx", key);
        }
    }

    [Fact]
    public void History_receipt_preview_stretches_rows_without_overriding_receipt_alignment()
    {
        var wpfRoot = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf");
        var view = XDocument.Load(Path.Combine(
            wpfRoot,
            "Views",
            "Screens",
            "TransactionHistoryView.xaml"));
        var theme = XDocument.Load(Path.Combine(wpfRoot, "Themes", "PosTheme.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var receiptRows = Assert.Single(view.Descendants(presentation + "ItemsControl").Where(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ReceiptPreviewRows}"));
        Assert.Equal("Stretch", (string?)receiptRows.Attribute("HorizontalAlignment"));

        var itemContainerStyle = Assert.Single(
            Assert.Single(receiptRows.Elements(presentation + "ItemsControl.ItemContainerStyle"))
                .Elements(presentation + "Style"));
        Assert.Equal("{x:Type ContentPresenter}", (string?)itemContainerStyle.Attribute("TargetType"));
        AssertSetter(itemContainerStyle, "HorizontalAlignment", "Stretch");

        var rowTemplate = Assert.Single(theme.Descendants(presentation + "DataTemplate").Where(element =>
            (string?)element.Attribute(x + "Key") == "ReceiptPreviewRowTemplate"));
        var textStyle = Assert.Single(rowTemplate.Descendants(presentation + "Style").Where(element =>
            (string?)element.Attribute("TargetType") == "TextBlock"));
        AssertSetter(textStyle, "TextAlignment", "Left");

        var centeredTrigger = Assert.Single(textStyle.Descendants(presentation + "DataTrigger").Where(element =>
            (string?)element.Attribute("Binding") == "{Binding IsCentered}" &&
            (string?)element.Attribute("Value") == "True"));
        AssertSetter(centeredTrigger, "TextAlignment", "Center");

        var rightAlignedTrigger = Assert.Single(textStyle.Descendants(presentation + "DataTrigger").Where(element =>
            (string?)element.Attribute("Binding") == "{Binding IsRightAligned}" &&
            (string?)element.Attribute("Value") == "True"));
        AssertSetter(rightAlignedTrigger, "TextAlignment", "Right");
    }

    [Fact]
    public void History_order_details_modal_keeps_touch_layout_and_product_identity_visible()
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
            "TransactionHistoryView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var overlay = Assert.Single(view.Descendants(presentation + "UserControl").Where(element =>
            (string?)element.Attribute(x + "Name") == "OrderDetailsOverlay"));
        Assert.Equal("True", (string?)overlay.Attribute("FocusManager.IsFocusScope"));
        Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.Equal("OrderDetailsOverlayPreviewKeyDown", (string?)overlay.Attribute("PreviewKeyDown"));
        Assert.Equal("True", (string?)overlay.Attribute("AutomationProperties.IsDialog"));
        Assert.Equal("{loc:Loc history.viewOrderDetails}", (string?)overlay.Attribute("AutomationProperties.Name"));
        Assert.Equal(
            "{Binding IsOrderDetailsOpen, Converter={StaticResource BoolToVis}}",
            (string?)overlay.Attribute("Visibility"));

        var dialog = Assert.Single(overlay.Elements(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "OrderDetailsDialog"));
        Assert.Equal("1000", (string?)dialog.Attribute("Width"));
        Assert.Equal("1000", (string?)dialog.Attribute("MaxWidth"));
        Assert.Equal("24", (string?)dialog.Attribute("Margin"));
        var statusText = Assert.Single(dialog.Descendants(presentation + "TextBlock").Where(element =>
            (string?)element.Attribute("Text") == "{Binding OrderDetailsStatusLabel}"));
        Assert.Equal("{DynamicResource PosTextSecondaryBrush}", (string?)statusText.Attribute("Foreground"));
        var statusBadge = Assert.IsType<XElement>(statusText.Parent);
        Assert.Equal("{DynamicResource PosSurfaceMutedBrush}", (string?)statusBadge.Attribute("Background"));
        Assert.Equal("{DynamicResource PosBorderBrush}", (string?)statusBadge.Attribute("BorderBrush"));

        var itemsGrid = Assert.Single(dialog.Descendants(presentation + "DataGrid").Where(element =>
            (string?)element.Attribute(x + "Name") == "OrderDetailsItemsGrid"));
        Assert.Equal("{Binding OrderDetailLines}", (string?)itemsGrid.Attribute("ItemsSource"));
        Assert.Equal("76", (string?)itemsGrid.Attribute("RowHeight"));
        Assert.Equal("76", (string?)itemsGrid.Attribute("MinRowHeight"));
        Assert.Equal("Disabled", (string?)itemsGrid.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));

        var productImage = Assert.Single(itemsGrid.Descendants(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "OrderDetailProductImage"));
        Assert.Equal("64", (string?)productImage.Attribute("Width"));
        Assert.Equal("64", (string?)productImage.Attribute("Height"));
        var imageBrush = Assert.Single(productImage.Descendants(presentation + "ImageBrush"));
        Assert.Contains(imageBrush.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".AsyncSourceText", StringComparison.Ordinal) &&
            attribute.Value == "{Binding ProductImage}");
        Assert.Single(productImage.Descendants().Where(element =>
            element.Name.LocalName == "PackIcon" &&
            (string?)element.Attribute("Kind") == "Shopping"));

        var metadata = Assert.Single(itemsGrid.Descendants(presentation + "TextBlock").Where(element =>
            (string?)element.Attribute(x + "Name") == "OrderDetailItemMetadata"));
        Assert.Equal("NoWrap", (string?)metadata.Attribute("TextWrapping"));
        Assert.Equal("CharacterEllipsis", (string?)metadata.Attribute("TextTrimming"));
        Assert.Contains(metadata.Elements(presentation + "Run"), run =>
            (string?)run.Attribute("Text") == "{loc:Loc ItemNumber}");
        Assert.Contains(metadata.Elements(presentation + "Run"), run =>
            (string?)run.Attribute("Text") == "{Binding ItemNumberDisplay, Mode=OneWay}");
        Assert.Contains(metadata.Elements(presentation + "Run"), run =>
            (string?)run.Attribute("Text") == "{Binding LookupCodeDisplay, Mode=OneWay}");
        Assert.DoesNotContain(metadata.DescendantsAndSelf(), element =>
            element.Attributes().Any(attribute =>
                attribute.Value.Contains("Barcode", StringComparison.OrdinalIgnoreCase)));
        Assert.Single(metadata.Descendants(presentation + "ToolTip"));

        var detailTextBindings = dialog.Descendants(presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .ToArray();
        Assert.Contains("{Binding DisplayReference}", detailTextBindings);
        Assert.Contains("{Binding CardSummary}", detailTextBindings);
        Assert.DoesNotContain("{Binding Reference}", detailTextBindings);
        Assert.Contains("{Binding PreviewSubtotal, StringFormat={}{0:C2}}", detailTextBindings);
        Assert.Contains("{Binding PreviewDiscount, StringFormat={}{0:C2}}", detailTextBindings);
        Assert.Contains("{Binding PreviewTotal, StringFormat={}{0:C2}}", detailTextBindings);
        Assert.Equal(3, dialog.Descendants().Count(element =>
            (string?)element.Attribute("Visibility") ==
                "{Binding IsOrderDetailsFinancialContentVisible, Converter={StaticResource BoolToVis}}"));
        Assert.Contains(dialog.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("AutomationProperties.LiveSetting") == "Assertive" &&
            (string?)element.Attribute("Text") == "{Binding OrderDetailsErrorMessage}");
        Assert.Equal(2, dialog.Descendants(presentation + "TextBlock").Count(element =>
            (string?)element.Attribute("AutomationProperties.LiveSetting") == "Polite"));

        Assert.Equal(2, dialog.Descendants(presentation + "Button").Count(element =>
            (string?)element.Attribute("Command") == "{Binding CloseOrderDetailsCommand}"));
        Assert.Single(dialog.Descendants(presentation + "Button").Where(element =>
            (string?)element.Attribute("Command") == "{Binding RetryOrderDetailsCommand}"));
        var escapeBinding = Assert.Single(overlay.Descendants(presentation + "KeyBinding").Where(element =>
            (string?)element.Attribute("Key") == "Escape"));
        Assert.Equal("{Binding CloseOrderDetailsCommand}", (string?)escapeBinding.Attribute("Command"));

        foreach (var key in new[]
                 {
                     "history.viewOrderDetails",
                     "history.closeOrderDetails",
                     "history.orderDetailsLoading",
                     "history.orderDetailsUnavailable",
                     "history.orderDetailsEmpty",
                     "history.remoteHeldDetailsUnavailable",
                     "history.retryOrderDetails",
                     "history.orderDetailsNoPayment",
                     "history.finalTotal",
                     "history.paidAmount",
                     "history.outstandingAmount",
                 })
        {
            AssertLocalizationKey(repoRoot, "Strings.resx", key);
            AssertLocalizationKey(repoRoot, "Strings.zh-CN.resx", key);
        }
    }

    [Fact]
    public void History_filters_use_two_rows_and_date_picker_inherits_dynamic_language()
    {
        var view = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "TransactionHistoryView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal("{Binding CurrentUiLanguage}", (string?)view.Root?.Attribute("Language"));

        var header = Assert.Single(view.Descendants(presentation + "Grid").Where(element =>
            (string?)element.Attribute(x + "Name") == "HistoryHeaderRegion"));
        var headerRows = Assert.Single(header.Elements(presentation + "Grid.RowDefinitions"));
        Assert.Equal(2, headerRows.Elements(presentation + "RowDefinition").Count());

        var sourceNavigation = Assert.Single(header.Descendants(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "HistorySourceNavigation"));
        var sourceTabs = sourceNavigation.Descendants(presentation + "RadioButton")
            .Where(element => (string?)element.Attribute("GroupName") == "HistorySource")
            .ToArray();
        Assert.Equal(4, sourceTabs.Length);
        Assert.All(sourceTabs, tab =>
        {
            Assert.Single(tab.Descendants().Where(element => element.Name.LocalName == "PackIcon"));
            Assert.Single(tab.Descendants(presentation + "TextBlock"));
        });

        var selectedTrigger = Assert.Single(
            FindStyle(view, x, "HistorySegmentRadioStyle")
                .Descendants(presentation + "Trigger")
                .Where(element =>
                    (string?)element.Attribute("Property") == "IsChecked" &&
                    (string?)element.Attribute("Value") == "True"));
        Assert.Contains(selectedTrigger.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "Root" &&
            (string?)setter.Attribute("Property") == "Background" &&
            (string?)setter.Attribute("Value") == "{DynamicResource PosPrimaryBrush}");
        Assert.Contains(selectedTrigger.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Foreground" &&
            (string?)setter.Attribute("Value") == "White");

        var filterPanel = Assert.Single(header.Descendants(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "HistoryFilterPanel"));
        Assert.Equal("1", (string?)filterPanel.Attribute("Grid.Row"));
        Assert.Equal("0,10,0,0", (string?)filterPanel.Attribute("Margin"));

        foreach (var filterName in new[]
                 {
                     "HistoryDateRangeFilter",
                     "HistoryTerminalFilter",
                     "HistorySearchFilter",
                 })
        {
            Assert.Single(filterPanel.Descendants().Where(element =>
                (string?)element.Attribute(x + "Name") == filterName));
        }

        var datePickers = filterPanel.Descendants(presentation + "DatePicker").ToArray();
        Assert.Equal(2, datePickers.Length);
        Assert.All(datePickers, picker =>
            Assert.Equal("Short", (string?)picker.Attribute("SelectedDateFormat")));
        Assert.Contains(datePickers, picker =>
            (string?)picker.Attribute("AutomationProperties.AutomationId") == "TransactionHistoryDateFrom");
        Assert.Contains(datePickers, picker =>
            (string?)picker.Attribute("AutomationProperties.AutomationId") == "TransactionHistoryDateTo");

        var loadButton = Assert.Single(filterPanel.Descendants(presentation + "Button").Where(element =>
            (string?)element.Attribute("Command") == "{Binding LoadCommand}"));
        Assert.Equal("{StaticResource PosPrimaryButtonStyle}", (string?)loadButton.Attribute("Style"));
    }

    [Fact]
    public void History_search_box_uses_content_aware_clear_button()
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
            "TransactionHistoryView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var searchTextBox = Assert.Single(view.Descendants(presentation + "TextBox").Where(element =>
            (string?)element.Attribute("Text") == "{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"));
        Assert.Equal("HistorySearchTextBox", (string?)searchTextBox.Attribute(x + "Name"));
        Assert.Equal("38,0,48,0", (string?)searchTextBox.Attribute("Padding"));
        Assert.Equal("False", (string?)searchTextBox.Attribute("InputMethod.IsInputMethodEnabled"));
        Assert.Equal("Off", (string?)searchTextBox.Attribute("InputMethod.PreferredImeState"));

        var enterBinding = Assert.Single(searchTextBox.Descendants(presentation + "KeyBinding").Where(element =>
            (string?)element.Attribute("Key") == "Enter"));
        Assert.Equal("{Binding LoadCommand}", (string?)enterBinding.Attribute("Command"));

        var clearButton = Assert.Single(view.Descendants(presentation + "Button").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "TransactionHistorySearchClearButton"));
        Assert.Equal("HistorySearchClearButton", (string?)clearButton.Attribute(x + "Name"));
        Assert.Equal("ClearSearchButtonClick", (string?)clearButton.Attribute("Click"));
        Assert.Equal("{loc:Loc Clear}", (string?)clearButton.Attribute("ToolTip"));
        Assert.Equal("{loc:Loc Clear}", (string?)clearButton.Attribute("AutomationProperties.Name"));
        Assert.Single(clearButton.Descendants().Where(element =>
            element.Name.LocalName == "PackIcon" &&
            (string?)element.Attribute("Kind") == "CloseCircle"));

        var clearButtonStyle = Assert.Single(clearButton.Descendants(presentation + "Style"));
        var visibilityTriggers = clearButtonStyle.Descendants(presentation + "DataTrigger").ToArray();
        Assert.Contains(visibilityTriggers, trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding Text, ElementName=HistorySearchTextBox}" &&
            (string?)trigger.Attribute("Value") == string.Empty);
        Assert.Contains(visibilityTriggers, trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding Text, ElementName=HistorySearchTextBox}" &&
            (string?)trigger.Attribute("Value") == "{x:Null}");

        var codeBehind = File.ReadAllText(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "TransactionHistoryView.xaml.cs"));
        Assert.Contains("HistorySearchTextBox.Clear();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("HistorySearchTextBox.Focus();", codeBehind, StringComparison.Ordinal);
    }

    private static void AssertTextStyleDoesNotWrap(
        XDocument document,
        XNamespace presentation,
        XNamespace x,
        string key)
    {
        var style = FindStyle(document, x, key);
        AssertSetter(style, "TextWrapping", "NoWrap");
        AssertSetter(style, "TextTrimming", "CharacterEllipsis");
        Assert.Equal("TextBlock", (string?)style.Attribute("TargetType"));
        Assert.NotEmpty(style.Elements(presentation + "Setter"));
    }

    private static XElement FindStyle(XDocument document, XNamespace x, string key) =>
        Assert.Single(document.Descendants().Where(element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute(x + "Key") == key));

    private static void AssertSetter(XElement style, string property, string value) =>
        Assert.Contains(style.Elements().Where(element => element.Name.LocalName == "Setter"), setter =>
            (string?)setter.Attribute("Property") == property && (string?)setter.Attribute("Value") == value);

    private static void AssertLocalizationKey(string repoRoot, string fileName, string key)
    {
        var resource = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Resources",
            fileName));
        Assert.Contains(resource.Descendants("data"), element =>
            (string?)element.Attribute("name") == key);
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
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
