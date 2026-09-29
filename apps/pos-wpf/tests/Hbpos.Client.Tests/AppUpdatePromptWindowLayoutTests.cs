using System.Xml.Linq;

namespace Hbpos.Client.Tests;

public sealed class AppUpdatePromptWindowLayoutTests
{
    private static readonly string[] PromptLocalizationKeys =
    [
        "appUpdate.optional.prompt.description",
        "appUpdate.optional.prompt.readyStatus",
        "appUpdate.optional.prompt.currentVersion",
        "appUpdate.optional.prompt.newVersion",
        "appUpdate.optional.prompt.packageDownloaded",
        "appUpdate.optional.prompt.whatsNew",
        "appUpdate.optional.prompt.noReleaseNotes",
        "appUpdate.optional.prompt.safety",
        "appUpdate.optional.prompt.installLater",
        "appUpdate.optional.prompt.restartAndInstall",
        "appUpdate.optional.prompt.settingsHint"
    ];

    [Fact]
    public void Update_prompt_uses_full_owner_scrim_and_centered_fluent_card()
    {
        var document = LoadPromptXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var window = Assert.IsType<XElement>(document.Root);

        Assert.Equal("None", (string?)window.Attribute("WindowStyle"));
        Assert.Equal("NoResize", (string?)window.Attribute("ResizeMode"));
        Assert.Equal("False", (string?)window.Attribute("ShowInTaskbar"));
        Assert.Equal("True", (string?)window.Attribute("AllowsTransparency"));

        var overlay = Assert.Single(window.Elements(presentation + "Grid"));
        Assert.Equal("ModalOverlay", (string?)overlay.Attribute(x + "Name"));
        Assert.Equal("#6B0F172A", (string?)overlay.Attribute("Background"));

        var card = Assert.Single(overlay.Elements(presentation + "Border").Where(element =>
            (string?)element.Attribute(x + "Name") == "AppUpdateDialogCard"));
        Assert.Equal("900", (string?)card.Attribute("MaxWidth"));
        Assert.Equal("640", (string?)card.Attribute("MaxHeight"));
        Assert.Equal("12", (string?)card.Attribute("CornerRadius"));
        Assert.Equal("{DynamicResource PosSurfaceBrush}", (string?)card.Attribute("Background"));
    }

    [Fact]
    public void Update_prompt_can_shrink_horizontally_without_clipping_on_1366x768_touch_displays()
    {
        var document = LoadPromptXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var window = Assert.IsType<XElement>(document.Root);
        var card = Assert.Single(window
            .Elements(presentation + "Grid")
            .Elements(presentation + "Border")
            .Where(element => (string?)element.Attribute(x + "Name") == "AppUpdateDialogCard"));

        var horizontalMinimums = new[]
        {
            (Element: "Window", Value: (string?)window.Attribute("MinWidth")),
            (Element: "AppUpdateDialogCard", Value: (string?)card.Attribute("MinWidth"))
        }.Where(item => item.Value is not null).ToArray();

        Assert.Empty(horizontalMinimums);
        Assert.Equal("Stretch", (string?)card.Attribute("HorizontalAlignment"));
        Assert.Equal("900", (string?)card.Attribute("MaxWidth"));
    }

    [Fact]
    public void Update_prompt_exposes_versions_release_notes_safety_and_safe_default_actions()
    {
        var document = LoadPromptXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains(document.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding CurrentVersion}");
        Assert.Contains(document.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding TargetVersion}");

        var releaseNotes = Assert.Single(document.Descendants(presentation + "ItemsControl").Where(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ReleaseNotes}"));
        Assert.Equal("{StaticResource AppUpdateReleaseNoteTemplate}", (string?)releaseNotes.Attribute("ItemTemplate"));

        var installLater = FindNamedButton(document, presentation, x, "InstallLaterButton");
        Assert.Equal("True", (string?)installLater.Attribute("IsDefault"));
        Assert.Equal("True", (string?)installLater.Attribute("IsCancel"));
        Assert.Equal("InstallLaterButton_Click", (string?)installLater.Attribute("Click"));
        Assert.Equal("{StaticResource PosSecondaryButtonStyle}", (string?)installLater.Attribute("Style"));

        var restartAndInstall = FindNamedButton(document, presentation, x, "RestartAndInstallButton");
        Assert.Null(restartAndInstall.Attribute("IsDefault"));
        Assert.Equal("RestartAndInstallButton_Click", (string?)restartAndInstall.Attribute("Click"));
        Assert.Equal("{StaticResource PosPrimaryButtonStyle}", (string?)restartAndInstall.Attribute("Style"));
    }

    [Fact]
    public void Update_prompt_replaces_native_message_box_and_localizes_every_visible_label()
    {
        var repoRoot = FindRepoRoot();
        var service = File.ReadAllText(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Services",
            "AppUpdatePromptService.cs"));

        Assert.DoesNotContain("MessageBox.Show", service, StringComparison.Ordinal);
        Assert.Contains("new AppUpdatePromptWindow", service, StringComparison.Ordinal);

        foreach (var resourceName in new[] { "Strings.resx", "Strings.zh-CN.resx" })
        {
            var resource = XDocument.Load(Path.Combine(
                repoRoot,
                "apps",
                "pos-wpf",
                "src",
                "Hbpos.Client.Wpf",
                "Resources",
                resourceName));

            foreach (var key in PromptLocalizationKeys)
            {
                Assert.Contains(resource.Descendants("data"), element =>
                    (string?)element.Attribute("name") == key &&
                    !string.IsNullOrWhiteSpace(element.Element("value")?.Value));
            }
        }
    }

    private static XElement FindNamedButton(
        XDocument document,
        XNamespace presentation,
        XNamespace x,
        string name)
    {
        return Assert.Single(document.Descendants(presentation + "Button").Where(element =>
            (string?)element.Attribute(x + "Name") == name));
    }

    private static XDocument LoadPromptXaml()
    {
        return XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Windows",
            "AppUpdatePromptWindow.xaml"));
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
