using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class AppUpdatePromptWindowRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    [Theory]
    // 中文注释：460×560 是启动页当 owner 时弹窗被压缩到的现场尺寸，按钮挤出卡片后收银机只能卡在弹窗上。
    [InlineData(460, 560, "en-US")]
    [InlineData(460, 560, "zh-CN")]
    [InlineData(1024, 728, "en-US")]
    [InlineData(1366, 728, "zh-CN")]
    public Task Action_buttons_stay_inside_the_card(int width, int height, string culture) =>
        host.RunAsync(_ =>
        {
            var localization = new LocalizationService();
            localization.SetCulture(culture);
            LocalizationResourceProvider.Instance.Configure(localization);
            var window = LoadPromptWindow();
            var overlay = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            window.Content = null;
            overlay.DataContext = new AppUpdatePromptViewModel(new AppUpdateCheckResponse
            {
                CurrentVersion = "1.0.28",
                TargetVersion = "1.0.29"
            });

            PaymentViewRuntimeStaTestHost.Realize(overlay, width, height);

            var card = FindNamed(window, "AppUpdateDialogCard");
            AssertInside(overlay, card);
            AssertInside(card, FindNamed(window, "InstallLaterButton"));
            AssertInside(card, FindNamed(window, "RestartAndInstallButton"));
            return Task.CompletedTask;
        });

    private static Window LoadPromptWindow()
    {
        // 中文注释：测试宿主的 pack://application 指向 testhost，窗口图标无法解析；
        // 这里只剥离 x:Class、事件与图标，按产品 XAML 原样验证布局。
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Windows",
            "AppUpdatePromptWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = Assert.IsType<XElement>(document.Root);
        root.Attribute(x + "Class")!.Remove();
        root.Attribute("Icon")!.Remove();
        root.Attribute("Loaded")!.Remove();
        foreach (var click in document.Descendants().Attributes("Click").ToArray())
        {
            click.Remove();
        }

        // 松散 XAML 解析时本项目的 clr-namespace 必须带程序集名（loc、services 等）；
        // 附加属性名也在该命名空间下，只能在序列化后的文本上替换声明。
        var xaml = Regex.Replace(
            document.ToString(),
            "\"clr-namespace:(Hbpos\\.Client\\.Wpf[^\";]*)\"",
            "\"clr-namespace:$1;assembly=Hbpos.Client.Wpf\"");
        return Assert.IsType<Window>(XamlReader.Parse(xaml));
    }

    private static FrameworkElement FindNamed(Window window, string name)
    {
        return Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
    }

    private static void AssertInside(FrameworkElement container, FrameworkElement element)
    {
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, $"{element.Name} 未完成布局。");
        var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
        var containerBounds = new Rect(container.RenderSize);
        Assert.True(
            containerBounds.Contains(bounds),
            $"{element.Name} {bounds} 超出 {container.Name} {containerBounds}。");
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
