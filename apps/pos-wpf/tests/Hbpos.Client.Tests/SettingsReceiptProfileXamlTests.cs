using System.Xml.Linq;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置页 XAML 与 ViewModel 只读状态的绑定契约：总部下发后资料字段（含代金券使用说明、分期条款）只读、硬件设置仍可编辑、
/// 按钮文字随状态切换。这些绑定无法在无界面的单测里渲染，用 XAML 结构断言防止被改丢。
/// </summary>
public sealed class SettingsReceiptProfileXamlTests
{
    private static readonly string[] ProfileFieldProperties =
    [
        "ReceiptBrandNameText",
        "ReceiptStoreNameText",
        "ReceiptStorePhoneText",
        "ReceiptStoreAddressText",
        "ReceiptAbnText",
        "ReceiptReturnPolicyText",
        "ReceiptVoucherTermsText",
        "ReceiptInstallmentTermsText"
    ];

    [Fact]
    public void The_receipt_profile_text_boxes_bind_read_only_to_the_headquarters_managed_state()
    {
        var document = LoadSettingsView();

        foreach (var property in ProfileFieldProperties)
        {
            var textBox = Assert.Single(document.Descendants(), element => IsTextBoxBoundTo(element, property));
            Assert.Contains(
                "IsReceiptProfileManaged",
                textBox.Attribute("IsReadOnly")?.Value ?? string.Empty,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ReceiptVoucherTermsText")]
    [InlineData("ReceiptInstallmentTermsText")]
    public void Terms_text_boxes_are_multiline_and_capped_at_600_characters(string property)
    {
        var textBox = Assert.Single(LoadSettingsView().Descendants(), element => IsTextBoxBoundTo(element, property));

        Assert.Equal("600", textBox.Attribute("MaxLength")?.Value);
        Assert.Equal("True", textBox.Attribute("AcceptsReturn")?.Value);
        Assert.Equal("Wrap", textBox.Attribute("TextWrapping")?.Value);
    }

    [Fact]
    public void Terms_text_boxes_show_the_leave_blank_for_default_hint_and_both_languages_have_the_copy()
    {
        var document = LoadSettingsView();

        // 两块文本框下各有一条「留空＝使用默认文案」提示。
        Assert.Equal(
            2,
            document.Descendants().Count(element =>
                element.Name.LocalName == "TextBlock" &&
                element.Attribute("Text")?.Value.Contains("settings.receiptPrinter.termsHelp", StringComparison.Ordinal) == true));

        var resourcesDirectory = Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Resources");
        foreach (var file in new[] { "SettingsStrings.resx", "SettingsStrings.zh-CN.resx" })
        {
            var keys = XDocument.Load(Path.Combine(resourcesDirectory, file))
                .Descendants("data")
                .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")!.Value);
            Assert.False(string.IsNullOrWhiteSpace(keys["settings.receiptPrinter.voucherTerms"]), file);
            Assert.False(string.IsNullOrWhiteSpace(keys["settings.receiptPrinter.installmentTerms"]), file);
            Assert.False(string.IsNullOrWhiteSpace(keys["settings.receiptPrinter.termsHelp"]), file);
        }
    }

    [Fact]
    public void Receipt_card_grid_rows_are_defined_once_and_every_section_has_its_own_row()
    {
        var document = LoadSettingsView();
        var save = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Button" &&
                       element.Attribute("Command")?.Value.Contains("SaveReceiptPrinterCommand", StringComparison.Ordinal) == true);
        // 小票资料卡片的行网格：包含保存按钮的最近一层带 RowDefinitions 的 Grid。
        var grid = save.Ancestors()
            .First(element => element.Name.LocalName == "Grid" &&
                              element.Elements().Any(child => child.Name.LocalName == "Grid.RowDefinitions"));

        var rowCount = grid.Element(grid.Name.Namespace + "Grid.RowDefinitions")!.Elements().Count();
        var childRows = grid.Elements()
            .Where(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .Select(child => int.Parse(child.Attribute("Grid.Row")?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        // 行数 = 区块数，且每个区块独占一行（新增条款区块后后面的区块行号顺延，不能叠在一起）。
        Assert.Equal(rowCount, childRows.Count);
        Assert.Equal(Enumerable.Range(0, rowCount), childRows.OrderBy(row => row));
        var termsRow = ParentRowOf(grid, "ReceiptVoucherTermsText");
        Assert.Equal(termsRow, ParentRowOf(grid, "ReceiptInstallmentTermsText"));
    }

    private static int ParentRowOf(XElement grid, string property)
    {
        var textBox = Assert.Single(grid.Descendants(), element => IsTextBoxBoundTo(element, property));
        var section = textBox.Ancestors().First(element => element.Parent == grid);
        return int.Parse(section.Attribute("Grid.Row")!.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Printer_hardware_text_boxes_stay_editable_when_the_profile_is_managed()
    {
        var document = LoadSettingsView();

        var portTextBox = Assert.Single(document.Descendants(), element => IsTextBoxBoundTo(element, "ReceiptPrinterPortText"));
        Assert.Null(portTextBox.Attribute("IsReadOnly"));
    }

    [Fact]
    public void Load_button_label_and_managed_hint_are_driven_by_the_view_model()
    {
        var document = LoadSettingsView();

        var button = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Button" &&
                       element.Attribute("Command")?.Value.Contains("LoadReceiptProfileCommand", StringComparison.Ordinal) == true);
        Assert.Contains(
            button.Descendants(),
            element => element.Name.LocalName == "TextBlock" &&
                       element.Attribute("Text")?.Value.Contains("ReceiptProfileLoadButtonText", StringComparison.Ordinal) == true);

        var hint = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "TextBlock" &&
                       element.Attribute("Text")?.Value.Contains("ReceiptProfileManagedHintText", StringComparison.Ordinal) == true);
        Assert.Contains(
            "IsReceiptProfileManaged",
            hint.Attribute("Visibility")?.Value ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static bool IsTextBoxBoundTo(XElement element, string property)
    {
        return element.Name.LocalName == "TextBox" &&
               element.Attribute("Text")?.Value.Contains("{Binding " + property + ",", StringComparison.Ordinal) == true;
    }

    private static XDocument LoadSettingsView()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "SettingsView.xaml");
        return XDocument.Load(path);
    }

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
}
