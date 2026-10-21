using System.Globalization;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

/// <summary>
/// 退货页 XAML 契约：只解析 XML，不需要 WPF 运行环境。
/// </summary>
public sealed class ReceiptReturnsViewXamlContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void No_receipt_mode_is_a_switch_not_a_checkbox()
    {
        var view = XDocument.Load(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Screens", "ReceiptReturnsView.xaml"));

        // 无小票退货是切换整个页面工作模式的开关，不是表单勾选项：不能退回勾选框。
        Assert.DoesNotContain(
            view.Descendants(Presentation + "CheckBox"),
            box => BindsNoReceiptMode(box));

        var toggle = Assert.Single(
            view.Descendants(Presentation + "ToggleButton"),
            candidate => BindsNoReceiptMode(candidate));
        // 与设置页的开关保持同一套样式；双向绑定，点开关就切换模式。
        Assert.Equal("{StaticResource MaterialDesignSwitchToggleButton}", (string?)toggle.Attribute("Style"));
        Assert.Contains("Mode=TwoWay", (string?)toggle.Attribute("IsChecked"), StringComparison.Ordinal);
        // 没有可见文字的开关必须有自动化名称（读屏 / UI 自动化），文字标签单独放在开关左边。
        Assert.Equal("{loc:Loc returns.noReceipt}", (string?)toggle.Attribute("AutomationProperties.Name"));
        // 触控区不小于标题栏里返回按钮的 44，不把标题栏撑高。
        Assert.True(
            double.Parse((string?)toggle.Attribute("MinHeight") ?? "0", CultureInfo.InvariantCulture) >= 44,
            "开关的触控高度应不小于 44");
    }

    private static bool BindsNoReceiptMode(XElement element) =>
        ((string?)element.Attribute("IsChecked"))?.Contains("IsNoReceiptMode", StringComparison.Ordinal) == true;

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
