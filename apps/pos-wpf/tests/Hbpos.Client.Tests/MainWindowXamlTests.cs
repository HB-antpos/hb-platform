using System.Xml.Linq;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class MainWindowXamlTests
{
    [Fact]
    public void Cashier_shell_uses_strict_1366_by_768_frame_with_54_and_42_pixel_chrome()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var window = Assert.IsType<XElement>(document.Root);
        Assert.Equal("1366", (string?)window.Attribute("Width"));
        Assert.Equal("768", (string?)window.Attribute("Height"));

        var shellGrid = Assert.Single(window.Elements(presentation + "Grid"));
        var rows = Assert.Single(shellGrid.Elements(presentation + "Grid.RowDefinitions"))
            .Elements(presentation + "RowDefinition")
            .Select(row => (string?)row.Attribute("Height"))
            .ToArray();
        Assert.Equal(["54", "*", "42"], rows);

        var pageTitle = Assert.Single(shellGrid.Descendants(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "{Binding ActivePageTitleText}"));
        Assert.Equal("Center", (string?)pageTitle.Attribute("HorizontalAlignment"));
        Assert.Equal("21", (string?)pageTitle.Attribute("FontSize"));
    }

    [Fact]
    public void Main_window_defaults_to_maximized_and_centers_normal_mode()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        var window = Assert.IsType<XElement>(document.Root);

        Assert.Equal("Maximized", (string?)window.Attribute("WindowState"));
        Assert.Equal("CenterScreen", (string?)window.Attribute("WindowStartupLocation"));
    }

    [Fact]
    public void Maximized_main_window_uses_zero_glass_custom_chrome_without_a_system_top_strip()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        XNamespace shell = "clr-namespace:System.Windows.Shell;assembly=PresentationFramework";
        var window = Assert.IsType<XElement>(document.Root);

        var chromeProperty = Assert.Single(window.Elements(shell + "WindowChrome.WindowChrome"));
        var chrome = Assert.Single(chromeProperty.Elements(shell + "WindowChrome"));

        Assert.Equal("0", (string?)chrome.Attribute("CaptionHeight"));
        Assert.Equal("6", (string?)chrome.Attribute("ResizeBorderThickness"));
        Assert.Equal("0", (string?)chrome.Attribute("CornerRadius"));
        Assert.Equal("0", (string?)chrome.Attribute("GlassFrameThickness"));
    }

    [Fact]
    public void Fallback_screen_content_is_bound_only_while_fallback_is_active()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var fallbackTrigger = Assert.Single(document.Descendants(presentation + "DataTrigger").Where(trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding IsFallbackScreenActive}"));
        var fallbackControl = Assert.IsType<XElement>(fallbackTrigger.Ancestors(presentation + "ContentControl").FirstOrDefault());

        Assert.Null(fallbackControl.Attribute("Content"));
        var style = Assert.Single(fallbackControl
            .Elements(presentation + "ContentControl.Style")
            .Elements(presentation + "Style"));
        var defaultSetters = style.Elements(presentation + "Setter").ToArray();
        Assert.Contains(defaultSetters, setter =>
            (string?)setter.Attribute("Property") == "Content" &&
            (string?)setter.Attribute("Value") == "{x:Null}");
        Assert.Contains(defaultSetters, setter =>
            (string?)setter.Attribute("Property") == "Visibility" &&
            (string?)setter.Attribute("Value") == "Collapsed");

        var activeSetters = fallbackTrigger.Elements(presentation + "Setter").ToArray();
        Assert.Contains(activeSetters, setter =>
            (string?)setter.Attribute("Property") == "Content" &&
            (string?)setter.Attribute("Value") == "{Binding CurrentScreen}");
        Assert.Contains(activeSetters, setter =>
            (string?)setter.Attribute("Property") == "Visibility" &&
            (string?)setter.Attribute("Value") == "Visible");
    }

    [Theory]
    [InlineData("{Binding CachedTransactionHistoryScreen}", "{Binding IsTransactionHistoryScreenActive}")]
    [InlineData("{Binding CachedSettingsScreen}", "{Binding IsSettingsScreenActive}")]
    public void Cached_heavy_screen_has_a_single_dedicated_host(string contentBinding, string activeBinding)
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var host = Assert.Single(document.Descendants(presentation + "ContentControl").Where(control =>
            (string?)control.Attribute("Content") == contentBinding));
        var trigger = Assert.Single(host.Descendants(presentation + "DataTrigger").Where(candidate =>
            (string?)candidate.Attribute("Binding") == activeBinding));

        Assert.Contains(trigger.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Visibility" &&
            (string?)setter.Attribute("Value") == "Visible");
    }

    [Fact]
    public void Cashier_login_overlay_restores_focus_when_server_switch_reenables_window()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var overlay = Assert.Single(document.Descendants(presentation + "Grid").Where(
            element => element.Attributes().Any(attribute =>
                string.Equals(attribute.Name.LocalName, "AutomationProperties.AutomationId", StringComparison.Ordinal) &&
                string.Equals(attribute.Value, "CashierLoginOverlay", StringComparison.Ordinal))));

        Assert.Equal(
            "CashierLoginOverlayIsEnabledChanged",
            (string?)overlay.Attribute("IsEnabledChanged"));
    }

    [Fact]
    public void Sync_center_order_timestamp_runs_use_one_way_bindings()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var createdAtRun = Assert.Single(document.Descendants(presentation + "Run").Where(
            element => ((string?)element.Attribute("Text"))?.Contains("CreatedAtDisplay", StringComparison.Ordinal) == true));
        var lastTriedAtRun = Assert.Single(document.Descendants(presentation + "Run").Where(
            element => ((string?)element.Attribute("Text"))?.Contains("LastTriedAtDisplay", StringComparison.Ordinal) == true));

        Assert.Equal("{Binding CreatedAtDisplay, Mode=OneWay}", (string?)createdAtRun.Attribute("Text"));
        Assert.Equal("{Binding LastTriedAtDisplay, Mode=OneWay}", (string?)lastTriedAtRun.Attribute("Text"));
    }

    [Fact]
    public void Sync_center_fills_main_content_area_without_fixed_size_limits()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var syncCenter = Assert.Single(document.Descendants(presentation + "Border").Where(
            element => string.Equals(
                (string?)element.Attribute("Visibility"),
                "{Binding IsSyncCenterExpanded, Converter={StaticResource BoolToVis}}",
                StringComparison.Ordinal)));

        Assert.Equal("Stretch", (string?)syncCenter.Attribute("HorizontalAlignment"));
        Assert.Equal("Stretch", (string?)syncCenter.Attribute("VerticalAlignment"));
        Assert.Equal("16", (string?)syncCenter.Attribute("Margin"));
        Assert.Null(syncCenter.Attribute("Width"));
        Assert.Null(syncCenter.Attribute("Height"));
        Assert.Null(syncCenter.Attribute("MaxWidth"));
        Assert.Null(syncCenter.Attribute("MaxHeight"));
    }

    [Fact]
    public void Footer_versions_bind_current_and_conditionally_show_upgrade_or_rollback_target()
    {
        var repoRoot = FindRepoRoot();
        var document = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        var attributeValues = document.Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToList();

        Assert.Contains("{Binding AppUpdate.CurrentVersion, Mode=OneWay}", attributeValues);
        Assert.Equal(
            2,
            attributeValues.Count(value =>
                string.Equals(value, "{Binding AppUpdate.TargetVersion, Mode=OneWay}", StringComparison.Ordinal)));
        Assert.Contains("{Binding AppUpdate.HasDifferentTargetVersion}", attributeValues);
        Assert.Contains("{Binding AppUpdate.IsRollbackTarget}", attributeValues);
        Assert.Contains("{loc:Loc shell.footer.currentVersion}", attributeValues);
        Assert.Contains("{loc:Loc shell.footer.latestVersion}", attributeValues);
        Assert.Contains("{loc:Loc shell.footer.targetVersion}", attributeValues);
        Assert.DoesNotContain("{Binding VersionStatusText}", attributeValues);

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
            var keys = resource.Descendants("data")
                .Select(element => (string?)element.Attribute("name"))
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("shell.footer.currentVersion", keys);
            Assert.Contains("shell.footer.latestVersion", keys);
            Assert.Contains("shell.footer.targetVersion", keys);
        }
    }

    [Fact]
    public void Header_brand_uses_shared_app_icon_and_hb_pos_name()
    {
        var repoRoot = FindRepoRoot();
        var document = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var brandMark = FindElementByAutomationId(document, "HeaderBrandMark");

        AssertSharedBrandImage(brandMark, "32", "32");
        Assert.Equal("0,0,10,0", (string?)brandMark.Attribute("Margin"));
        Assert.DoesNotContain(document.Descendants(presentation + "TextBlock"), element =>
            string.Equals((string?)element.Attribute("Text"), "HB", StringComparison.Ordinal));

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
            var appName = Assert.Single(resource.Descendants("data").Where(element =>
                string.Equals((string?)element.Attribute("name"), "AppName", StringComparison.Ordinal)));

            Assert.Equal("HB POS", (string?)appName.Element("value"));
        }
    }

    [Fact]
    public void Startup_brand_uses_shared_app_icon_without_legacy_frame_or_initials()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "StartupSplashWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var window = Assert.IsType<XElement>(document.Root);

        var brandMark = FindElementByAutomationId(document, "StartupBrandMark");

        AssertSharedBrandImage(brandMark, "56", "56", StartupSplashAutomationName);
        // 图标放在品牌橙色区的半透明圆角底板里。
        Assert.Equal(presentation + "Border", brandMark.Parent?.Name);
        Assert.Equal("64", (string?)brandMark.Parent?.Attribute("Width"));
        Assert.Equal("Center", (string?)brandMark.Attribute("VerticalAlignment"));
        Assert.DoesNotContain(document.Descendants(presentation + "TextBlock"), element =>
            string.Equals((string?)element.Attribute("Text"), "HB", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Descendants().Attributes(), attribute =>
            attribute.Value is "#FFE8F0FE" or "#FFD3E2FF" or "#FFEE5835" or "#FFD94827");

        Assert.Equal(
            "pack://application:,,,/Resources/AppIcon.ico",
            (string?)window.Attribute("Icon"));
    }

    [Fact]
    public void Startup_splash_shows_current_version_and_update_notice()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "StartupSplashWindow.xaml"));

        var footer = FindElementByAutomationId(document, "StartupVersionFooter");
        var versionText = FindElementByAutomationId(document, "StartupVersionText");
        var notice = FindElementByAutomationId(document, "StartupUpdateNotice");

        Assert.Contains(footer.Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding VersionLabel}");
        Assert.Equal("{Binding VersionText}", (string?)versionText.Attribute("Text"));
        Assert.Equal(
            "{Binding HasUpdateNotice, Converter={StaticResource BoolToVis}}",
            (string?)notice.Attribute("Visibility"));
        Assert.Contains(notice.Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding UpdateNoticeText}");
    }

    [Fact]
    public void Startup_splash_is_self_contained_because_it_runs_on_its_own_ui_thread()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "StartupSplashWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var window = Assert.IsType<XElement>(document.Root);
        var resources = Assert.Single(window.Elements(), element => element.Name.LocalName == "Window.Resources");
        var localKeys = resources.Elements()
            .Select(element => (string?)element.Attribute(xaml + "Key"))
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);
        var attributeValues = document.Descendants().Attributes().Select(attribute => attribute.Value).ToList();
        var staticKeys = attributeValues
            .SelectMany(value => System.Text.RegularExpressions.Regex.Matches(value, @"\{StaticResource ([^}]+)\}"))
            .Select(match => match.Groups[1].Value.Trim())
            .ToList();

        // 启动页线程不能读取主线程正在合并的 App 资源：只允许引用窗口内联的资源，也不允许 DynamicResource。
        Assert.NotEmpty(staticKeys);
        Assert.All(staticKeys, key => Assert.Contains(key, localKeys));
        Assert.DoesNotContain(attributeValues, value => value.Contains("DynamicResource", StringComparison.Ordinal));
        Assert.DoesNotContain(attributeValues, value => value.Contains("loc:Loc", StringComparison.Ordinal));
        Assert.Equal("Segoe UI", (string?)window.Attribute("FontFamily"));

        // 不进任务栏：WPF 会给它挂隐藏 owner，UI 自动化按 MainWindowHandle 取到的仍是收银主窗口。
        Assert.Equal("False", (string?)window.Attribute("ShowInTaskbar"));
        Assert.Equal("True", (string?)window.Attribute("Topmost"));

        var progressBar = FindElementByAutomationId(document, "StartupProgressBar");
        Assert.Equal("ProgressBar", progressBar.Name.LocalName);
        Assert.Equal("{Binding ProgressValue, Mode=OneWay}", (string?)progressBar.Attribute("Value"));
        Assert.Equal("{Binding StageTitle}", (string?)FindElementByAutomationId(document, "StartupStageTitle").Attribute("Text"));
        Assert.Equal("{Binding StageDetail}", (string?)FindElementByAutomationId(document, "StartupStageDetail").Attribute("Text"));
        var stepList = FindElementByAutomationId(document, "StartupStepList");
        Assert.Equal("{Binding Steps}", (string?)stepList.Attribute("ItemsSource"));
        Assert.Equal("{Binding StepText}", (string?)stepList.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding ElapsedText}", (string?)FindElementByAutomationId(document, "StartupElapsed").Attribute("Text"));
        Assert.Contains(FindElementByAutomationId(document, "StartupPercent").Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding PercentText, Mode=OneWay}");
    }

    [Fact]
    public void App_theme_dictionaries_load_after_splash_in_original_order()
    {
        var appXaml = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "App.xaml"));

        // 写在 App.xaml 里的字典会在启动页出现之前同步解析，这里必须保持为空。
        Assert.DoesNotContain(appXaml.Descendants(), element =>
            element.Name.LocalName is "ResourceDictionary.MergedDictionaries" or "BundledTheme");
        Assert.Equal(
            new[]
            {
                "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml",
                "pack://application:,,,/Hbpos.Client.Wpf;component/Themes/Palettes/Blue.xaml",
                "pack://application:,,,/Hbpos.Client.Wpf;component/Themes/PosTheme.xaml"
            },
            App.DeferredResourceDictionarySources.ToArray());

        // 首帧色板与默认配色一致，配色切换按同一个地址替换；测试进程里先注册 pack 协议再构造 Uri。
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        Assert.Equal(
            WpfColorThemeApplier.GetPaletteUri(ColorThemeService.FallbackTheme).OriginalString,
            App.DeferredResourceDictionarySources[1]);
    }

    [Fact]
    public void Device_registration_and_all_windows_use_shared_app_icon()
    {
        var repoRoot = FindRepoRoot();
        var wpfRoot = Path.Combine(repoRoot, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");
        const string sharedImageSource = "pack://application:,,,/Resources/AppBrandIcon.png";
        const string windowIcon = "pack://application:,,,/Resources/AppIcon.ico";

        var registration = XDocument.Load(Path.Combine(
            wpfRoot,
            "Views",
            "Screens",
            "DeviceRegistrationView.xaml"));
        var registrationIcon = Assert.Single(registration.Descendants().Where(element =>
            string.Equals(element.Name.LocalName, "Image", StringComparison.Ordinal) &&
            string.Equals((string?)element.Attribute("Source"), sharedImageSource, StringComparison.Ordinal)));
        AssertSharedBrandImage(registrationIcon, "42", "42");

        foreach (var relativeWindowPath in new[]
                 {
                     "MainWindow.xaml",
                     "StartupSplashWindow.xaml",
                     Path.Combine("Views", "Windows", "CustomerDisplayWindow.xaml"),
                     Path.Combine("Views", "Windows", "AppUpdatePromptWindow.xaml")
                 })
        {
            var window = XDocument.Load(Path.Combine(wpfRoot, relativeWindowPath));
            Assert.Equal(windowIcon, (string?)window.Root?.Attribute("Icon"));
        }
    }

    [Fact]
    public void Brand_images_use_consistent_scaling_pixel_alignment_and_accessible_name()
    {
        var wpfRoot = Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");
        var mainWindow = XDocument.Load(Path.Combine(wpfRoot, "MainWindow.xaml"));
        var startup = XDocument.Load(Path.Combine(wpfRoot, "StartupSplashWindow.xaml"));
        var registration = XDocument.Load(Path.Combine(
            wpfRoot,
            "Views",
            "Screens",
            "DeviceRegistrationView.xaml"));
        var registrationIcon = Assert.Single(registration.Descendants().Where(element =>
            string.Equals(element.Name.LocalName, "Image", StringComparison.Ordinal) &&
            string.Equals(
                (string?)element.Attribute("Source"),
                "pack://application:,,,/Resources/AppBrandIcon.png",
                StringComparison.Ordinal)));

        AssertSharedBrandImage(FindElementByAutomationId(mainWindow, "HeaderBrandMark"), "32", "32");
        AssertSharedBrandImage(FindElementByAutomationId(startup, "StartupBrandMark"), "56", "56", StartupSplashAutomationName);
        AssertSharedBrandImage(registrationIcon, "42", "42");
    }

    private static XElement FindElementByAutomationId(XDocument document, string automationId)
    {
        return Assert.Single(document.Descendants().Where(element =>
            element.Attributes().Any(attribute =>
                string.Equals(
                    attribute.Name.LocalName,
                    "AutomationProperties.AutomationId",
                    StringComparison.Ordinal) &&
                string.Equals(attribute.Value, automationId, StringComparison.Ordinal))));
    }

    // 启动页在独立线程上运行，不绑定主线程的本地化单例，品牌图名称改绑启动页自己的标题（同为 "HB POS"）。
    private const string StartupSplashAutomationName = "{Binding TitleText}";

    private static void AssertSharedBrandImage(
        XElement image,
        string width,
        string height,
        string automationName = "{loc:Loc AppName}")
    {
        Assert.Equal("Image", image.Name.LocalName);
        Assert.Equal(width, (string?)image.Attribute("Width"));
        Assert.Equal(height, (string?)image.Attribute("Height"));
        Assert.Equal("pack://application:,,,/Resources/AppBrandIcon.png", (string?)image.Attribute("Source"));
        Assert.Equal("Uniform", (string?)image.Attribute("Stretch"));
        Assert.Equal("HighQuality", GetAttributeValue(image, "RenderOptions.BitmapScalingMode"));
        Assert.Equal("True", (string?)image.Attribute("SnapsToDevicePixels"));
        Assert.Equal("True", (string?)image.Attribute("UseLayoutRounding"));
        Assert.Equal(automationName, GetAttributeValue(image, "AutomationProperties.Name"));
    }

    private static string? GetAttributeValue(XElement element, string localName)
    {
        return element.Attributes()
            .SingleOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value;
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
