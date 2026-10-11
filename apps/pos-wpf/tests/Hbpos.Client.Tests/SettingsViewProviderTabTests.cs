using System.Xml.Linq;

namespace Hbpos.Client.Tests;

public sealed class SettingsViewProviderTabTests
{
    [Fact]
    public void Payment_provider_tabs_bind_selected_index_two_way_to_the_active_processor_tab()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Screens", "SettingsView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var tabs = Assert.Single(document.Descendants(presentation + "TabControl").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "PaymentProviderTabs"));

        Assert.Equal(
            "{Binding SelectedProviderTabIndex, Mode=TwoWay}",
            (string?)tabs.Attribute("SelectedIndex"));
        // 标签顺序必须与 SettingsViewModel.SelectedProviderTabIndex 的约定一致：0 = Square，1 = Linkly。
        var headers = tabs.Elements(presentation + "TabItem").Select(item => item.ToString()).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.Contains("SquareTitleText", headers[0]);
        Assert.Contains("LinklyTitleText", headers[1]);
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