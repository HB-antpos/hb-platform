using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

/// <summary>
/// 屏幕键盘符号键契约：货号、规格、姓名常含 - _ / . '，按键缺失会让收银员无法输入。
/// 只做 XAML 结构断言（纯文本解析，不依赖 WPF 运行时）；按键点击后的文本注入由各 ViewModel 测试覆盖。
/// </summary>
public sealed class OnScreenKeyboardSymbolKeysLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Pos_terminal_search_keyboard_bottom_row_has_hyphen_underscore_slash_and_dot_between_space_and_search()
    {
        var view = LoadView("PosTerminalView.xaml");

        // 底行 = 含 Clear 键与 Enter 键的那一个 Grid，按 Grid.Column 排序后的 CommandParameter 即键序。
        var bottomRow = Assert.Single(view.Descendants(Presentation + "Grid").Where(grid =>
            grid.Elements(Presentation + "Button").Any(button =>
                IsCommand(button, "NumberInputCommand") && Parameter(button) == "Enter")));

        var parameters = bottomRow.Elements(Presentation + "Button")
            .OrderBy(button => int.Parse((string?)button.Attribute("Grid.Column") ?? "0"))
            .Select(Parameter)
            .ToArray();

        Assert.Equal(new[] { "Clear", "Space", "-", "_", "/", ".", "Enter" }, parameters);

        // 每个按钮占一列，列定义数量与按钮数一致，不挤压其他键。
        Assert.Equal(
            parameters.Length,
            bottomRow.Element(Presentation + "Grid.ColumnDefinitions")!.Elements().Count());
    }

    [Fact]
    public void Receipt_returns_open_item_description_keyboard_has_digit_row_and_symbol_keys()
    {
        var view = LoadView("ReceiptReturnsView.xaml");

        var descriptionKeyboard = Assert.Single(view.Descendants(Presentation + "StackPanel").Where(panel =>
            ((string?)panel.Attribute("Visibility"))?.Contains("IsOpenItemDescriptionKeyboardVisible", StringComparison.Ordinal) == true));

        var parameters = descriptionKeyboard.Descendants(Presentation + "Button")
            .Where(button => IsCommand(button, "OpenItemKeyboardInputCommand"))
            .Select(Parameter)
            .ToHashSet();

        foreach (var key in "0123456789-./".Select(character => character.ToString()))
        {
            Assert.Contains(key, parameters);
        }

        // Back 只出现一次（已从底行上移到数字行），避免重复键。
        Assert.Single(descriptionKeyboard.Descendants(Presentation + "Button").Where(button =>
            IsCommand(button, "OpenItemKeyboardInputCommand") && Parameter(button) == "Back"));
    }

    [Fact]
    public void Payment_installment_customer_name_keyboard_has_hyphen_apostrophe_and_dot()
    {
        var view = LoadView("PaymentView.xaml");

        var nameKeyboard = Assert.Single(view.Descendants(Presentation + "UniformGrid").Where(grid =>
            ((string?)grid.Attribute("Visibility"))?.Contains("IsInstallmentCustomerNameDraftActive", StringComparison.Ordinal) == true));
        var buttons = nameKeyboard.Elements(Presentation + "Button").ToArray();
        var parameters = buttons.Select(Parameter).ToHashSet();

        foreach (var key in new[] { "-", "'", "." })
        {
            Assert.Contains(key, parameters);
        }

        // 6 列网格键数须是 6 的整数倍，否则最后一行会留半行空缺。
        Assert.Equal(0, buttons.Length % 6);
    }

    private static bool IsCommand(XElement button, string commandName)
    {
        return ((string?)button.Attribute("Command"))?.Contains(commandName, StringComparison.Ordinal) == true;
    }

    private static string? Parameter(XElement button)
    {
        return (string?)button.Attribute("CommandParameter");
    }

    private static XDocument LoadView(string fileName)
    {
        return XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            fileName));
    }

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
