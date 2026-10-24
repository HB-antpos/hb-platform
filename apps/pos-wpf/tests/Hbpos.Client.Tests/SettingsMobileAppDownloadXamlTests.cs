using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置页「员工 App 下载」的 XAML 契约：分类入口、两张二维码卡片、放大层都靠绑定接线，
/// 无界面的单测渲染不了，用 XAML 结构断言防止被改丢；同时保证引用的文案键在中英文资源里都存在。
/// </summary>
public sealed partial class SettingsMobileAppDownloadXamlTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void The_category_list_has_a_button_that_selects_the_mobile_app_download_page()
    {
        var button = Assert.Single(
            LoadSettingsView().Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding SelectMobileAppDownloadCommand}");

        Assert.Equal("{Binding IsMobileAppDownloadSelected}", (string?)button.Attribute("Tag"));
        Assert.Equal("{StaticResource SettingsCategoryButtonStyle}", (string?)button.Attribute("Style"));
    }

    [Fact]
    public void The_page_is_only_visible_when_its_category_is_selected()
    {
        // 页面根是 SettingsPageHost（带 Visibility 绑定），原页面内容（一个 Grid）整体作为它的 Content。
        var panel = Assert.Single(
            LoadSettingsView().Descendants().Where(element => element.Name.LocalName == "SettingsPageHost"),
            element => ((string?)element.Attribute("Visibility") ?? string.Empty).Contains("IsMobileAppDownloadSelected", StringComparison.Ordinal));
        Assert.Equal("{StaticResource SettingsPageHostStyle}", (string?)panel.Attribute("Style"));

        var pageRoot = Assert.Single(panel.Elements(Presentation + "Grid"));
        var inner = Assert.Single(pageRoot.Elements(Presentation + "Grid"));
        Assert.Equal("{Binding MobileAppDownload}", (string?)inner.Attribute("DataContext"));
    }

    [Theory]
    [InlineData("IosQrPayload")]
    [InlineData("AndroidQrPayload")]
    [InlineData("EnlargedQrPayload")]
    public void Every_QR_image_renders_through_the_QR_converter(string payloadProperty)
    {
        var image = Assert.Single(
            LoadSettingsView().Descendants(Presentation + "Image"),
            element => ((string?)element.Attribute("Source") ?? string.Empty).Contains(payloadProperty, StringComparison.Ordinal));

        Assert.Contains("StaticResource QrCodeImage", (string?)image.Attribute("Source"), StringComparison.Ordinal);
        // 像素对齐渲染，避免缩放后扫码边缘发糊。
        Assert.Equal("None", (string?)image.Attribute("Stretch"));
    }

    [Fact]
    public void Tapping_a_QR_code_enlarges_it_and_tapping_the_overlay_closes_it()
    {
        var document = LoadSettingsView();
        var enlargeButtons = document.Descendants(Presentation + "Button")
            .Where(element => (string?)element.Attribute("Command") == "{Binding EnlargeCommand}")
            .Select(element => (string?)element.Attribute("CommandParameter") ?? string.Empty)
            .ToArray();

        Assert.Equal(["ios", "android"], enlargeButtons);

        var closeBinding = Assert.Single(
            document.Descendants(Presentation + "MouseBinding"),
            element => ((string?)element.Attribute("Command") ?? string.Empty).Contains("CloseEnlargedCommand", StringComparison.Ordinal));
        Assert.Equal("LeftClick", (string?)closeBinding.Attribute("Gesture"));
    }

    [Fact]
    public void The_unpublished_android_state_replaces_the_QR_instead_of_showing_a_dead_code()
    {
        var document = LoadSettingsView();
        var androidQrButton = Assert.Single(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("CommandParameter") == "android"
                && (string?)element.Attribute("Command") == "{Binding EnlargeCommand}");

        Assert.Contains("IsAndroidAvailable", (string?)androidQrButton.Attribute("Visibility"), StringComparison.Ordinal);
        Assert.Single(
            document.Descendants(Presentation + "Border"),
            element => ((string?)element.Attribute("Visibility") ?? string.Empty).Contains("IsAndroidNotPublished", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_mobile_app_text_key_used_by_the_page_exists_in_both_languages()
    {
        var xaml = File.ReadAllText(SettingsViewPath());
        var keys = LocKeyRegex().Matches(xaml).Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(keys);

        foreach (var resource in new[] { "SettingsStrings.resx", "SettingsStrings.zh-CN.resx" })
        {
            var names = XDocument.Load(Path.Combine(ResourcesDirectory(), resource))
                .Descendants("data")
                .Select(element => (string?)element.Attribute("name"))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                Assert.True(names.Contains(key), $"{resource} 缺少文案键 {key}");
            }
        }
    }

    [GeneratedRegex(@"\{loc:Loc ((?:mobileApp\.|settings\.category\.mobileAppDownload)[A-Za-z0-9_.]*)\}")]
    private static partial Regex LocKeyRegex();

    private static XDocument LoadSettingsView() => XDocument.Load(SettingsViewPath());

    private static string SettingsViewPath() =>
        Path.Combine(WpfSourceDirectory(), "Views", "Screens", "SettingsView.xaml");

    private static string ResourcesDirectory() => Path.Combine(WpfSourceDirectory(), "Resources");

    private static string WpfSourceDirectory() =>
        Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "apps", "pos-wpf")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
