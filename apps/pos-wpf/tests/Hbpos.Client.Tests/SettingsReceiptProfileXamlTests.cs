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
    public void Receipt_sub_pages_stack_in_one_body_and_the_save_button_lives_in_the_footer()
    {
        var document = LoadSettingsView();
        var save = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Button" &&
                       element.Attribute("Command")?.Value.Contains("SaveReceiptPrinterCommand", StringComparison.Ordinal) == true);
        // 首屏重设计后「保存小票打印机」在固定底栏里，只在没开自动保存时显示（命令与原来相同）。
        Assert.Contains(save.Ancestors(), element => element.Name.LocalName == "SettingsPageHost.Footer");
        Assert.Contains(
            "IsExplicitSaveVisible",
            save.Attribute("Visibility")?.Value ?? string.Empty,
            StringComparison.Ordinal);

        // 三个分页的内容容器叠在同一个正文单元格里，各自按所选分页互斥显示，互不嵌套。
        var hardwarePage = SubPageOf(document, "ReceiptPrinterPortText");
        var storePage = SubPageOf(document, "ReceiptBrandNameText");
        var termsPage = SubPageOf(document, "ReceiptVoucherTermsText");
        Assert.Contains("IsReceiptHardwareSubTabSelected", hardwarePage.Attribute("Visibility")!.Value, StringComparison.Ordinal);
        Assert.Contains("IsReceiptStoreSubTabSelected", storePage.Attribute("Visibility")!.Value, StringComparison.Ordinal);
        Assert.Contains("IsReceiptTermsSubTabSelected", termsPage.Attribute("Visibility")!.Value, StringComparison.Ordinal);
        Assert.Same(hardwarePage.Parent, storePage.Parent);
        Assert.Same(hardwarePage.Parent, termsPage.Parent);

        // 条款三栏（退换货政策 / 代金券 / 分期条款）都在条款分页里。
        Assert.Same(termsPage, SubPageOf(document, "ReceiptInstallmentTermsText"));
        Assert.Same(termsPage, SubPageOf(document, "ReceiptReturnPolicyText"));
        // 「立即同步 / 从分店资料载入」是同一个命令，在底栏里供门店信息、小票条款两个分页共用。
        var sync = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Button" &&
                       element.Attribute("Command")?.Value.Contains("LoadReceiptProfileCommand", StringComparison.Ordinal) == true);
        Assert.Contains(sync.Ancestors(), element => element.Name.LocalName == "SettingsPageHost.Footer");
    }

    /// <summary>文本框所在的分页容器：最近一层按「所选分页」显示 / 折叠的祖先。</summary>
    private static XElement SubPageOf(XDocument document, string property)
    {
        var textBox = Assert.Single(document.Descendants(), element => IsTextBoxBoundTo(element, property));
        return textBox.Ancestors().First(element =>
            element.Attribute("Visibility")?.Value.Contains("SubTabSelected", StringComparison.Ordinal) == true);
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
