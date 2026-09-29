using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

public sealed class SpecialProductsViewLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Special_products_workspace_matches_cashier_home_density_and_sidebar()
    {
        var view = LoadView();
        var workspace = FindNamedElement(view, "SpecialProductsWorkspace");
        var columns = workspace.Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .ToArray();

        Assert.Equal(["84*", "16*"], columns.Select(column => (string?)column.Attribute("Width")));
        Assert.Equal(["760", "180"], columns.Select(column => (string?)column.Attribute("MinWidth")));
        Assert.Equal("{DynamicResource PosSurfaceAltBrush}", (string?)workspace.Attribute("Background"));

        var actionPanel = FindNamedElement(view, "SpecialActionsPanel");
        Assert.Equal("2", (string?)actionPanel.Attribute("Rows"));
        Assert.Equal("2", (string?)actionPanel.Attribute("Columns"));
        Assert.Equal("5,7,5,6", (string?)actionPanel.Attribute("Margin"));
        var actionButtons = actionPanel.Elements(Presentation + "Button").ToArray();
        Assert.Equal(
            [
                "{Binding BackCommand}",
                "{Binding DownloadCommand}",
                "{Binding ToggleEditModeCommand}",
                "{Binding RefreshCommand}",
            ],
            actionButtons.Select(button => (string?)button.Attribute("Command")));
        Assert.Equal(
            "{StaticResource SpecialSidebarPrimaryButtonStyle}",
            (string?)actionButtons[1].Attribute("Style"));

        var flatButtonStyle = FindStyle(view, "SpecialFlatButtonBaseStyle");
        var flatButtonRoot = Assert.Single(flatButtonStyle.Descendants(Presentation + "Border"));
        Assert.Equal("5", (string?)flatButtonRoot.Attribute("CornerRadius"));
        Assert.Empty(flatButtonStyle.Descendants(Presentation + "DropShadowEffect"));

        var panelStyle = FindStyle(view, "SpecialPanelStyle");
        AssertSetter(panelStyle, "CornerRadius", "5");
        AssertSetter(panelStyle, "BorderThickness", "1");
        AssertSetter(panelStyle, "BorderBrush", "{DynamicResource PosBorderBrush}");

        var actionStyle = FindStyle(view, "SpecialSidebarActionButtonStyle");
        AssertSetter(actionStyle, "MinHeight", "84");
        AssertSetter(actionStyle, "Margin", "3");
    }

    [Fact]
    public void Special_product_grid_keeps_twenty_item_page_and_product_interactions()
    {
        var view = LoadView();
        var productGrid = FindNamedElement(view, "SpecialProductGrid");
        Assert.Equal("{Binding PagedSpecialItems}", (string?)productGrid.Attribute("ItemsSource"));
        Assert.Equal(
            "{Binding SelectedSpecialItem, Mode=TwoWay}",
            (string?)productGrid.Attribute("SelectedItem"));

        var uniformGrid = Assert.Single(productGrid
            .Element(Presentation + "ListBox.ItemsPanel")!
            .Descendants(Presentation + "UniformGrid"));
        Assert.Equal("4", (string?)uniformGrid.Attribute("Rows"));
        Assert.Equal("5", (string?)uniformGrid.Attribute("Columns"));

        var cardButton = Assert.Single(productGrid.Descendants(Presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("SpecialItemCardCommand", StringComparison.Ordinal) == true));
        Assert.Equal("{Binding}", (string?)cardButton.Attribute("CommandParameter"));
        Assert.Equal("{StaticResource PosSearchSegmentButtonStyle}", (string?)cardButton.Attribute("Style"));

        var image = Assert.Single(cardButton.Descendants(Presentation + "Image"));
        Assert.Contains(image.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".AsyncSourceText", StringComparison.Ordinal) &&
            attribute.Value == "{Binding ProductImage}");

        Assert.Contains(cardButton.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding DisplayName}");
        Assert.Contains(cardButton.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding ItemNumber}");
        Assert.Contains(cardButton.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding LookupCode}");
        var price = Assert.Single(cardButton.Descendants(Presentation + "TextBlock").Where(text =>
            ((string?)text.Attribute("Text"))?.Contains("{Binding RetailPrice", StringComparison.Ordinal) == true));
        Assert.Equal("{DynamicResource PosAccentBrush}", (string?)price.Attribute("Foreground"));

        var selectedTrigger = Assert.Single(view.Descendants(Presentation + "DataTrigger").Where(trigger =>
            trigger.Elements(Presentation + "Setter").Any(setter =>
                (string?)setter.Attribute("TargetName") == "SpecialProductCard")));
        AssertTriggerSetter(selectedTrigger, "BorderBrush", "{DynamicResource PosPrimaryBrush}");
        AssertTriggerSetter(selectedTrigger, "BorderThickness", "4,1,1,1");
        AssertTriggerSetter(selectedTrigger, "Background", "{DynamicResource PosPrimarySoftBrush}");
    }

    [Fact]
    public void Special_products_redesign_preserves_all_command_and_edit_state_bindings()
    {
        var view = LoadView();
        var bindingOccurrences = view.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Value.Contains("{Binding", StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .ToArray();
        var requiredBindings = new[]
        {
            "{Binding BackCommand}",
            "{Binding BackText}",
            "{Binding ClearSearchCommand}",
            "{Binding ClearSearchText}",
            "{Binding DataContext.AddSpecialProductCommand, RelativeSource={RelativeSource AncestorType=ListBox}}",
            "{Binding DataContext.AddText, RelativeSource={RelativeSource AncestorType=ListBox}}",
            "{Binding DataContext.AreThumbnailsEnabled, RelativeSource={RelativeSource AncestorType=ListBox}}",
            "{Binding DataContext.SpecialItemCardCommand, RelativeSource={RelativeSource AncestorType=ListBox}}",
            "{Binding DisplayName}",
            "{Binding DownloadCommand}",
            "{Binding DownloadProgressDetailText}",
            "{Binding DownloadProgressText}",
            "{Binding DownloadProgressValue}",
            "{Binding DownloadText}",
            "{Binding EditModeText}",
            "{Binding EmptyText}",
            "{Binding HasSearchResults, Converter={StaticResource BoolToVis}}",
            "{Binding IsDownloadProgressVisible, Converter={StaticResource BoolToVis}}",
            "{Binding IsEditMode, Converter={StaticResource BoolToVis}}",
            "{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}",
            "{Binding IsSpecialListEmpty, Converter={StaticResource BoolToVis}}",
            "{Binding ItemNumber}",
            "{Binding LookupCode}",
            "{Binding MoveDownCommand}",
            "{Binding MoveDownText}",
            "{Binding MoveUpCommand}",
            "{Binding MoveUpText}",
            "{Binding NextPageCommand}",
            "{Binding NextPageText}",
            "{Binding OnlineStateText}",
            "{Binding PagedSpecialItems}",
            "{Binding PageStatusText}",
            "{Binding PreviousPageCommand}",
            "{Binding PreviousPageText}",
            "{Binding ProductImage}",
            "{Binding RefreshCommand}",
            "{Binding RefreshText}",
            "{Binding RemoveSpecialProductCommand}",
            "{Binding RemoveText}",
            "{Binding RetailPrice, StringFormat={}{0:C2}}",
            "{Binding SearchButtonText}",
            "{Binding SearchCommand}",
            "{Binding SearchPlaceholderText}",
            "{Binding SearchResults}",
            "{Binding SearchResultsText}",
            "{Binding SearchText, UpdateSourceTrigger=PropertyChanged}",
            "{Binding SelectedSearchResult, Mode=TwoWay}",
            "{Binding SelectedSpecialItem, Mode=TwoWay}",
            "{Binding SelectedSpecialItem}",
            "{Binding SelectedSpecialItemText}",
            "{Binding StatusMessage}",
            "{Binding SubtitleText}",
            "{Binding ToggleEditModeCommand}",
            "{Binding}",
        };
        Assert.All(requiredBindings, binding => Assert.Contains(binding, bindingOccurrences));

        var repeatedBindingMinimums = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["{Binding DataContext.AreThumbnailsEnabled, RelativeSource={RelativeSource AncestorType=ListBox}}"] = 2,
            ["{Binding DisplayName}"] = 2,
            ["{Binding IsEditMode, Converter={StaticResource BoolToVis}}"] = 2,
            ["{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}"] = 2,
            ["{Binding LookupCode}"] = 2,
            ["{Binding ProductImage}"] = 2,
            ["{Binding SearchCommand}"] = 2,
            ["{Binding SelectedSpecialItem}"] = 3,
            ["{Binding}"] = 2,
        };
        Assert.All(repeatedBindingMinimums, pair =>
            Assert.True(
                bindingOccurrences.Count(binding => binding == pair.Key) >= pair.Value,
                $"Expected at least {pair.Value} occurrences of {pair.Key}."));

        var commands = view.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "Command")
            .Select(attribute => attribute.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var expectedCommands = new[]
        {
            "{Binding BackCommand}",
            "{Binding ClearSearchCommand}",
            "{Binding DataContext.AddSpecialProductCommand, RelativeSource={RelativeSource AncestorType=ListBox}}",
            "{Binding DataContext.SpecialItemCardCommand, RelativeSource={RelativeSource AncestorType=ListBox}}",
            "{Binding DownloadCommand}",
            "{Binding MoveDownCommand}",
            "{Binding MoveUpCommand}",
            "{Binding NextPageCommand}",
            "{Binding PreviousPageCommand}",
            "{Binding RefreshCommand}",
            "{Binding RemoveSpecialProductCommand}",
            "{Binding SearchCommand}",
            "{Binding SearchCommand}",
            "{Binding ToggleEditModeCommand}",
        }.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedCommands, commands);

        var visibilityBindings = view.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "Visibility" &&
                                attribute.Value.Contains("{Binding", StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "{Binding HasSearchResults, Converter={StaticResource BoolToVis}}",
                "{Binding IsDownloadProgressVisible, Converter={StaticResource BoolToVis}}",
                "{Binding IsEditMode, Converter={StaticResource BoolToVis}}",
                "{Binding IsEditMode, Converter={StaticResource BoolToVis}}",
                "{Binding IsSpecialListEmpty, Converter={StaticResource BoolToVis}}",
            }.OrderBy(value => value, StringComparer.Ordinal),
            visibilityBindings);

        var editSearch = FindNamedElement(view, "SpecialEditSearchPanel");
        var editActions = FindNamedElement(view, "SpecialEditActionsPanel");
        Assert.Equal(
            "{Binding IsEditMode, Converter={StaticResource BoolToVis}}",
            (string?)editSearch.Attribute("Visibility"));
        Assert.Equal(
            "{Binding IsEditMode, Converter={StaticResource BoolToVis}}",
            (string?)editActions.Attribute("Visibility"));

        var progress = FindNamedElement(view, "SpecialDownloadProgressPanel");
        Assert.Equal(
            "{Binding IsDownloadProgressVisible, Converter={StaticResource BoolToVis}}",
            (string?)progress.Attribute("Visibility"));

        var searchResults = Assert.Single(editSearch.Descendants(Presentation + "ListBox").Where(list =>
            (string?)list.Attribute("ItemsSource") == "{Binding SearchResults}"));
        Assert.Equal(
            "{Binding SelectedSearchResult, Mode=TwoWay}",
            (string?)searchResults.Attribute("SelectedItem"));

        var addButton = Assert.Single(searchResults.Descendants(Presentation + "Button").Where(element =>
            ((string?)element.Attribute("Command"))?.Contains("AddSpecialProductCommand", StringComparison.Ordinal) == true));
        Assert.Equal("{Binding}", (string?)addButton.Attribute("CommandParameter"));
        Assert.Equal(
            "{Binding DataContext.AddText, RelativeSource={RelativeSource AncestorType=ListBox}}",
            (string?)addButton.Attribute("ToolTip"));

        var downloadButton = Assert.Single(view.Descendants(Presentation + "Button").Where(element =>
            (string?)element.Attribute("Command") == "{Binding DownloadCommand}"));
        Assert.Contains(downloadButton.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".Cue", StringComparison.Ordinal) &&
            attribute.Value == "Download");

        foreach (var command in new[] { "MoveUpCommand", "MoveDownCommand", "RemoveSpecialProductCommand" })
        {
            var button = Assert.Single(editActions.Descendants(Presentation + "Button").Where(element =>
                ((string?)element.Attribute("Command"))?.Contains(command, StringComparison.Ordinal) == true));
            Assert.Equal("{Binding SelectedSpecialItem}", (string?)button.Attribute("CommandParameter"));
        }

        var removeButton = Assert.Single(editActions.Descendants(Presentation + "Button").Where(element =>
            (string?)element.Attribute("Command") == "{Binding RemoveSpecialProductCommand}"));
        Assert.Contains(removeButton.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith(".Cue", StringComparison.Ordinal) &&
            attribute.Value == "Delete");
    }

    private static XDocument LoadView() => XDocument.Load(Path.Combine(
        FindRepoRoot(),
        "apps",
        "pos-wpf",
        "src",
        "Hbpos.Client.Wpf",
        "Views",
        "Screens",
        "SpecialProductsView.xaml"));

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
