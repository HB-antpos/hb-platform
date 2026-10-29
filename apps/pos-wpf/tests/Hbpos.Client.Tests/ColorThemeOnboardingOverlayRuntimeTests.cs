using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Views.Controls;

namespace Hbpos.Client.Tests;

/// <summary>
/// 首次登录配色选择窗按真实色板渲染：六张卡片、只有当前预选项带勾、确认按钮写明所选配色；
/// 点卡片预览后整屏色板跟着换，勾也跟着移动。设置 HBPOS_THEME_SCREENSHOTS 时输出截图供目测。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class ColorThemeOnboardingOverlayRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    private const int Width = 1366;
    private const int Height = 768;

    [Fact]
    public Task Overlay_shows_six_cards_checks_the_preselected_one_and_follows_the_preview() => host.RunAsync(async application =>
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        LocalizationResourceProvider.Instance.Configure(localization);
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Blue, applier: new WpfColorThemeApplier());
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.NotSet);
        var onboarding = fixture.Onboarding();
        var overlay = new ColorThemeOnboardingOverlay { DataContext = onboarding };
        var window = new Window { Content = overlay };
        try
        {
            await onboarding.HandleCashierLoginAsync(fixture.Login());
            PaymentViewRuntimeStaTestHost.Realize(overlay, Width, Height);
            SaveScreenshot(overlay, "onboarding-blue.png");

            Assert.True(onboarding.IsOpen);
            var cards = Descendants<Button>(overlay).Where(button => button.CommandParameter is PosColorTheme).ToList();
            Assert.Equal(6, cards.Count);
            Assert.Equal([PosColorTheme.Blue], CheckedThemes(cards));
            // 主色按钮上的文字必须是白字（隐式 TextBlock 样式曾把它盖成正文深色）。
            var confirmText = Descendants<TextBlock>(overlay).Single(text => text.Text == "使用「蓝色」开始收银");
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(confirmText.Foreground).Color);
            Assert.Contains(Descendants<TextBlock>(overlay), text => text.Text == "欢迎，Alice");

            cards.Single(card => (PosColorTheme)card.CommandParameter == PosColorTheme.Graphite).Command.Execute(PosColorTheme.Graphite);
            PaymentViewRuntimeStaTestHost.Realize(overlay, Width, Height);
            SaveScreenshot(overlay, "onboarding-graphite.png");

            Assert.Equal("Graphite", application.Resources["PosPaletteId"]);
            Assert.Equal([PosColorTheme.Graphite], CheckedThemes(cards));
            Assert.Contains(Descendants<TextBlock>(overlay), text => text.Text == "使用「石墨夜」开始收银");
        }
        finally
        {
            // 共享测试 Application 的其他用例依赖默认色板。
            new WpfColorThemeApplier().Apply(PosColorTheme.Default);
            window.Close();
        }
    });

    [Fact]
    public Task Overlay_stays_hidden_until_a_cashier_needs_to_choose() => host.RunAsync(async _ =>
    {
        var fixture = new CashierColorThemeFixture();
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.Found, PosColorTheme.Paper);
        var onboarding = fixture.Onboarding();
        var overlay = new ColorThemeOnboardingOverlay { DataContext = onboarding };
        var window = new Window { Content = overlay };
        try
        {
            await onboarding.HandleCashierLoginAsync(fixture.Login());
            PaymentViewRuntimeStaTestHost.Realize(overlay, Width, Height);

            Assert.False(onboarding.IsOpen);
            Assert.False(overlay.FindName("OverlayRoot") is UIElement { IsVisible: true });
        }
        finally
        {
            window.Close();
        }
    });

    // 卡片右上角的勾在模板里，选中时显示。
    private static IEnumerable<PosColorTheme> CheckedThemes(IEnumerable<Button> cards) =>
        cards
            .Where(card => card.Template.FindName("Check", card) is UIElement { Visibility: Visibility.Visible })
            .Select(card => (PosColorTheme)card.CommandParameter);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void SaveScreenshot(FrameworkElement view, string fileName)
    {
        var output = Environment.GetEnvironmentVariable("HBPOS_THEME_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, fileName));
        encoder.Save(file);
    }
}
