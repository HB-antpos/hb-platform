using System.Reflection;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Services;
using Hbpos.Updater;

namespace Hbpos.Client.Tests;

public sealed class UpdaterPackagingTests
{
    [Theory]
    [InlineData("zh-CN", "正在更新 HB POS")]
    [InlineData("zh-Hans", "正在更新 HB POS")]
    [InlineData("en-AU", "Updating HB POS")]
    [InlineData(null, "Updating HB POS")]
    public void Strings_follow_the_cashier_culture(string? culture, string expectedTitle)
    {
        Assert.Equal(expectedTitle, UpdaterStrings.ForCulture(culture).UpdatingTitle);
    }

    [Fact]
    public void Chinese_and_english_strings_are_complete_and_share_placeholders()
    {
        var properties = typeof(UpdaterStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string));

        foreach (var property in properties)
        {
            var chinese = (string)property.GetValue(UpdaterStrings.ChineseSimplified)!;
            var english = (string)property.GetValue(UpdaterStrings.English)!;

            Assert.False(string.IsNullOrWhiteSpace(chinese), property.Name);
            Assert.False(string.IsNullOrWhiteSpace(english), property.Name);
            Assert.Equal(chinese.Contains("{0}", StringComparison.Ordinal), english.Contains("{0}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Updater_executable_name_matches_client_staging_contract()
    {
        var project = XDocument.Load(Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Updater", "Hbpos.Updater.csproj"));

        Assert.Equal(
            Path.GetFileNameWithoutExtension(AppUpdateProgressWindowOptions.UpdaterExecutableName),
            typeof(UpdaterOptions).Assembly.GetName().Name);
        Assert.Equal("WinExe", project.Descendants("OutputType").Single().Value);
    }

    [Fact]
    public void Updater_reuses_client_brand_artwork_instead_of_a_copy()
    {
        var updaterRoot = Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Updater");
        var project = File.ReadAllText(Path.Combine(updaterRoot, "Hbpos.Updater.csproj"));
        var window = XDocument.Load(Path.Combine(updaterRoot, "UpdaterWindow.xaml"));

        Assert.Contains("<ApplicationIcon>..\\Hbpos.Client.Wpf\\Resources\\AppIcon.ico</ApplicationIcon>", project);
        Assert.Contains("Include=\"..\\Hbpos.Client.Wpf\\Resources\\AppBrandIcon.png\" Link=\"Resources\\AppBrandIcon.png\"", project);
        Assert.False(Directory.Exists(Path.Combine(updaterRoot, "Resources")));
        Assert.Equal("pack://application:,,,/Resources/AppIcon.ico", (string?)window.Root?.Attribute("Icon"));
        Assert.Contains(window.Descendants(), element =>
            element.Name.LocalName == "Image" &&
            (string?)element.Attribute("Source") == "pack://application:,,,/Resources/AppBrandIcon.png");
    }

    [Fact]
    public void Updater_window_exposes_failure_actions_for_automation()
    {
        var window = XDocument.Load(Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Updater", "UpdaterWindow.xaml"));
        var automationIds = window.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "AutomationProperties.AutomationId")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "UpdaterTitle",
                "UpdaterPercent",
                "UpdaterProgressBar",
                "UpdaterSteps",
                "UpdaterFailurePanel",
                "UpdaterRetryButton",
                "UpdaterOpenCurrentButton",
                "UpdaterViewLogButton"
            },
            automationIds);
    }

    [Fact]
    public void Solution_builds_the_updater_project()
    {
        var solution = File.ReadAllText(Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "hbpos_win.slnx"));

        Assert.Contains("<Project Path=\"src/Hbpos.Updater/Hbpos.Updater.csproj\" />", solution);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "apps", "pos-wpf")) &&
                Directory.Exists(Path.Combine(current.FullName, "services", "backend")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
