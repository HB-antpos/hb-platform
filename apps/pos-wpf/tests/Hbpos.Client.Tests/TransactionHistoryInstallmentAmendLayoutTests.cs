using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

/// <summary>历史页订单明细弹窗“修改商品”编辑区的 XAML 契约与中英文资源完整性测试（不需要 WPF 运行时）。</summary>
public sealed class TransactionHistoryInstallmentAmendLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Footer_edit_button_is_gated_by_the_view_model_visibility_and_is_a_touch_target()
    {
        var dialog = LoadDialog();

        var edit = Assert.Single(dialog.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsEditItemsButton");

        Assert.Equal("{Binding BeginEditInstallmentLinesCommand}", (string?)edit.Attribute("Command"));
        Assert.Equal(
            "{Binding IsEditInstallmentLinesVisible, Converter={StaticResource BoolToVis}}",
            (string?)edit.Attribute("Visibility"));
        Assert.Equal("TransactionHistoryOrderDetailsEditItemsButton", (string?)edit.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("44", (string?)edit.Attribute("Height"));
        Assert.Equal("{StaticResource PosSecondaryButtonStyle}", (string?)edit.Attribute("Style"));
        Assert.Contains(edit.Descendants(), element =>
            element.Name.LocalName == "PackIcon" && (string?)element.Attribute("Kind") == "PencilOutline");
        Assert.Contains(edit.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{loc:Loc history.installment.editItems}");
    }

    [Fact]
    public void Footer_swaps_close_for_cancel_and_save_while_editing_without_adding_more_close_commands()
    {
        var dialog = LoadDialog();

        // 弹窗里仍然只有两个 Close 命令按钮（头部 X + 底栏关闭），既有契约不变。
        var closeButtons = dialog.Descendants(Presentation + "Button")
            .Where(element => (string?)element.Attribute("Command") == "{Binding CloseOrderDetailsCommand}")
            .ToList();
        Assert.Equal(2, closeButtons.Count);
        var footerClose = Assert.Single(closeButtons, element =>
            (string?)element.Attribute("Visibility") ==
            "{Binding IsInstallmentLinesReadOnlyMode, Converter={StaticResource BoolToVis}}");
        Assert.Equal("44", (string?)footerClose.Attribute("Height"));

        var cancel = Assert.Single(dialog.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsEditCancelButton");
        Assert.Equal("{Binding CancelInstallmentLinesEditCommand}", (string?)cancel.Attribute("Command"));
        Assert.Equal(
            "{Binding IsEditingInstallmentLines, Converter={StaticResource BoolToVis}}",
            (string?)cancel.Attribute("Visibility"));
        Assert.Equal("{StaticResource PosSecondaryButtonStyle}", (string?)cancel.Attribute("Style"));
        Assert.Equal("44", (string?)cancel.Attribute("Height"));

        var save = Assert.Single(dialog.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsEditSaveButton");
        Assert.Equal("{Binding SaveInstallmentLinesCommand}", (string?)save.Attribute("Command"));
        Assert.Equal("{Binding InstallmentEditorSaveButtonText}", (string?)save.Attribute("Content"));
        Assert.Equal(
            "{Binding IsEditingInstallmentLines, Converter={StaticResource BoolToVis}}",
            (string?)save.Attribute("Visibility"));
        Assert.Equal("{StaticResource PosPrimaryButtonStyle}", (string?)save.Attribute("Style"));
        Assert.Equal("44", (string?)save.Attribute("Height"));

        // 继续付款按钮沿用原有命令与可见性绑定（编辑期间由 VM 属性隐藏）。
        var continuePayment = Assert.Single(dialog.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsContinuePaymentButton");
        Assert.Equal(
            "{Binding IsContinueInstallmentPaymentVisible, Converter={StaticResource BoolToVis}}",
            (string?)continuePayment.Attribute("Visibility"));
    }

    [Fact]
    public void Item_card_switches_between_the_read_only_grid_and_the_edit_panel()
    {
        var dialog = LoadDialog();

        var readOnlyHost = Assert.Single(dialog.Descendants(Presentation + "Grid"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsReadOnlyItemsHost");
        Assert.Equal(
            "{Binding IsInstallmentLinesReadOnlyMode, Converter={StaticResource BoolToVis}}",
            (string?)readOnlyHost.Attribute("Visibility"));
        var itemsGrid = Assert.Single(readOnlyHost.Descendants(Presentation + "DataGrid"));
        Assert.Equal("OrderDetailsItemsGrid", (string?)itemsGrid.Attribute(X + "Name"));
        Assert.Equal("{Binding OrderDetailLines}", (string?)itemsGrid.Attribute("ItemsSource"));

        var panel = Assert.Single(dialog.Descendants(Presentation + "Grid"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsEditPanel");
        Assert.Equal(
            "{Binding IsEditingInstallmentLines, Converter={StaticResource BoolToVis}}",
            (string?)panel.Attribute("Visibility"));
        // 保存期间整个编辑区禁用，授权弹窗 / 网络请求等待时不可再改动。
        Assert.Equal("{Binding IsInstallmentEditorInputEnabled}", (string?)panel.Attribute("IsEnabled"));
        Assert.Same(readOnlyHost.Parent, panel.Parent);

        var notice = Assert.Single(dialog.Descendants(Presentation + "Border"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsEditNotice");
        Assert.Equal(
            "{Binding HasInstallmentEditorNotice, Converter={StaticResource BoolToVis}}",
            (string?)notice.Attribute("Visibility"));
        Assert.Contains(notice.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding InstallmentEditorNotice}");
    }

    [Fact]
    public void Add_product_search_supports_enter_scan_and_touch_selection()
    {
        var panel = LoadEditPanel();

        var searchBox = Assert.Single(panel.Descendants(Presentation + "TextBox"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsEditSearchBox");
        Assert.Equal(
            "{Binding InstallmentLineSearchText, UpdateSourceTrigger=PropertyChanged}",
            (string?)searchBox.Attribute("Text"));
        Assert.Equal("44", (string?)searchBox.Attribute("Height"));
        var enter = Assert.Single(searchBox.Descendants(Presentation + "KeyBinding"));
        Assert.Equal("Return", (string?)enter.Attribute("Key"));
        Assert.Equal("{Binding SearchInstallmentLineProductsCommand}", (string?)enter.Attribute("Command"));

        var searchButton = Assert.Single(panel.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "TransactionHistoryEditItemsSearchButton");
        Assert.Equal("{Binding SearchInstallmentLineProductsCommand}", (string?)searchButton.Attribute("Command"));

        var candidates = Assert.Single(panel.Descendants(Presentation + "ItemsControl"), element =>
            (string?)element.Attribute("ItemsSource") == "{Binding InstallmentLineSearchResults}");
        var candidateButton = Assert.Single(candidates.Descendants(Presentation + "Button"));
        Assert.Contains("AddInstallmentLineProductCommand", (string?)candidateButton.Attribute("Command"), StringComparison.Ordinal);
        Assert.Equal("{Binding}", (string?)candidateButton.Attribute("CommandParameter"));
        // 候选区只在有结果时出现，“未找到”提示独立显示。
        var candidatesBorder = Assert.IsType<XElement>(candidates.Ancestors(Presentation + "Border").First());
        Assert.Equal(
            "{Binding HasInstallmentLineSearchResults, Converter={StaticResource BoolToVis}}",
            (string?)candidatesBorder.Attribute("Visibility"));
        Assert.Contains(panel.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding InstallmentLineSearchMessage}");
    }

    [Fact]
    public void Editable_rows_expose_stepper_price_box_and_delete_with_touch_sized_controls()
    {
        var panel = LoadEditPanel();
        var rows = Assert.Single(panel.Descendants(Presentation + "ItemsControl"), element =>
            (string?)element.Attribute("ItemsSource") == "{Binding InstallmentEditor.Rows}");

        var buttons = rows.Descendants(Presentation + "Button").ToList();
        foreach (var command in new[] { "DecrementCommand", "IncrementCommand", "RemoveCommand" })
        {
            var button = Assert.Single(buttons, element => (string?)element.Attribute("Command") == $"{{Binding {command}}}");
            // 图标按钮统一走 44×44 的 HistoryIconButtonStyle。
            Assert.Equal("{StaticResource HistoryIconButtonStyle}", (string?)button.Attribute("Style"));
            Assert.NotNull(button.Attribute("AutomationProperties.Name"));
        }

        var quantity = Assert.Single(rows.Descendants(Presentation + "TextBox"), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "TransactionHistoryEditItemsQuantity");
        Assert.Equal("{Binding QuantityText, UpdateSourceTrigger=PropertyChanged}", (string?)quantity.Attribute("Text"));
        Assert.Equal("44", (string?)quantity.Attribute("Height"));
        var price = Assert.Single(rows.Descendants(Presentation + "TextBox"), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "TransactionHistoryEditItemsUnitPrice");
        Assert.Equal("{Binding UnitPriceText, UpdateSourceTrigger=PropertyChanged}", (string?)price.Attribute("Text"));
        Assert.Equal("44", (string?)price.Attribute("Height"));

        // 非法输入用红框标出：数量 / 单价文本框各有一个按对应无效标志触发的 DataTrigger。
        foreach (var flag in new[] { "IsQuantityInvalid", "IsUnitPriceInvalid" })
        {
            Assert.Contains(rows.Descendants(Presentation + "DataTrigger"), trigger =>
                (string?)trigger.Attribute("Binding") == $"{{Binding {flag}}}" &&
                (string?)trigger.Attribute("Value") == "True");
        }

        Assert.Contains(rows.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding ActualAmount, StringFormat={}{0:C2}}");
        Assert.Contains(rows.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding MetadataDisplay}");
    }

    [Fact]
    public void Summary_strip_shows_new_total_paid_new_balance_and_validation_feedback()
    {
        var panel = LoadEditPanel();
        var texts = panel.Descendants(Presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .ToList();

        Assert.Contains("{Binding InstallmentEditor.NewTotal, StringFormat={}{0:C2}}", texts);
        Assert.Contains("{Binding InstallmentEditor.PaidAmount, StringFormat={}{0:C2}}", texts);
        Assert.Contains("{Binding InstallmentEditor.NewBalance, StringFormat={}{0:C2}}", texts);
        Assert.Contains("{Binding InstallmentEditor.Delta, StringFormat={}{0:+0.00;-0.00;0.00}}", texts);

        var message = Assert.Single(panel.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding InstallmentEditorMessage}");
        Assert.Equal("{DynamicResource PosDangerTextBrush}", (string?)message.Attribute("Foreground"));
        Assert.Equal(
            "{Binding InstallmentEditorMessage, Converter={StaticResource StringHasValueToVis}}",
            (string?)message.Attribute("Visibility"));
        var hint = Assert.Single(panel.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding InstallmentEditorHint}");
        Assert.Equal("{DynamicResource PosSuccessTextBrush}", (string?)hint.Attribute("Foreground"));
    }

    [Fact]
    public void Edit_texts_are_localized_in_both_languages_and_placeholders_match()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(ViewDirectory(root), "TransactionHistoryView.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "ViewModels", "TransactionHistoryViewModel.cs"));
        var english = LoadResources(root, "Strings.resx");
        var chinese = LoadResources(root, "Strings.zh-CN.resx");

        // XAML 里用到的 history.installment.edit* 键 + VM 里 TOrFallback 用到的键，都必须两种语言齐全。
        var keys = Regex.Matches(xaml, @"\{loc:Loc (history\.installment\.edit\w+)\}")
            .Select(match => match.Groups[1].Value)
            .Concat(Regex.Matches(viewModel, "\"(history\\.installment\\.edit\\w+)\"").Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(keys.Count >= 25, $"Expected the edit feature to use many localized keys, found {keys.Count}.");
        foreach (var key in keys)
        {
            Assert.True(english.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en), $"Missing English text for {key}");
            Assert.True(chinese.TryGetValue(key, out var zh) && !string.IsNullOrWhiteSpace(zh), $"Missing Chinese text for {key}");
            // 占位符数量必须一致，否则 string.Format 在某个语言下会抛异常或漏参。
            Assert.Equal(
                Regex.Matches(en!, @"\{\d+\}").Count,
                Regex.Matches(zh!, @"\{\d+\}").Count);
            Assert.NotEqual(en, zh);
        }
    }

    private static XElement LoadDialog()
    {
        var view = XDocument.Load(Path.Combine(ViewDirectory(FindRepoRoot()), "TransactionHistoryView.xaml"));
        var overlay = Assert.Single(view.Descendants(Presentation + "UserControl"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsOverlay");
        return Assert.Single(overlay.Elements(Presentation + "Border"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsDialog");
    }

    private static XElement LoadEditPanel() =>
        Assert.Single(LoadDialog().Descendants(Presentation + "Grid"), element =>
            (string?)element.Attribute(X + "Name") == "OrderDetailsEditPanel");

    private static string ViewDirectory(string root) =>
        Path.Combine(root, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Screens");

    private static Dictionary<string, string> LoadResources(string root, string fileName)
    {
        var document = XDocument.Load(Path.Combine(
            root, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Resources", fileName));
        return document.Descendants("data")
            .GroupBy(element => (string)element.Attribute("name")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);
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
