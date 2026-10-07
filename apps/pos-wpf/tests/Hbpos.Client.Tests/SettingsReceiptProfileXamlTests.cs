using System.Xml.Linq;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置页 XAML 与 ViewModel 只读状态的绑定契约：总部下发后六个资料字段只读、硬件设置仍可编辑、
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
        "ReceiptReturnPolicyText"
    ];

    [Fact]
    public void The_six_receipt_profile_text_boxes_bind_read_only_to_the_headquarters_managed_state()
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
