using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;
using MaterialDesignThemes.Wpf;

namespace Hbpos.Client.Tests;

// 真机巡检发现：深色主题下输入框文字和未着色图标落回系统黑色，这里按真实色板渲染设置页逐项核对。
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class ColorThemeSettingsReadabilityTests(PaymentViewRuntimeStaTestHost host)
{
    private const int Width = 1366;
    private const int Height = 1500;

    [Theory]
    [InlineData(PosColorTheme.Default)]
    [InlineData(PosColorTheme.Paper)]
    [InlineData(PosColorTheme.Graphite)]
    [InlineData(PosColorTheme.Celadon)]
    public Task Settings_inputs_and_icons_stay_readable_in_every_color_theme(PosColorTheme theme) => host.RunAsync(async _ =>
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        LocalizationResourceProvider.Instance.Configure(localization);
        var setup = DispatchProxy.Create<ICardTerminalSetupService, SettingsPaymentMethodsViewRuntimeTests.UnavailableTerminalSetup>();
        using var http = new HttpClient();
        var apiSettings = new ApiServerSettingsViewModel(
            new ApiServerSettingsService(http, () => "https://example.test/pos-api/", _ => throw new NotSupportedException()),
            localization);
        using var vm = new SettingsViewModel(setup, localization, apiServerSettings: apiSettings);
        await vm.LoadAsync();
        vm.ReceiptPrinterPortText = "USB";
        vm.ReceiptStoreNameText = "TestStore";
        vm.SquareLocations.Add(new SquareLocationOption("L1", "Main Branch"));
        vm.SelectedSquareLocation = vm.SquareLocations[0];

        var view = new SettingsView { DataContext = vm };
        // 与主窗口一致：窗口前景色跟随色板，未单独着色的图标继承它；应用级色板变化也只通知窗口内的元素。
        var window = new Window { Content = view };
        window.SetResourceReference(Control.ForegroundProperty, "PosTextBrush");
        var applier = new WpfColorThemeApplier();
        try
        {
            applier.Apply(theme);
            var failures = new List<string>();
            var categories = new (string Name, CommunityToolkit.Mvvm.Input.IRelayCommand Command)[]
            {
                ("data", vm.SelectDataMaintenanceCommand),
                ("printer", vm.SelectReceiptPrinterCommand),
                ("device", vm.SelectDeviceRegistrationCommand),
                ("terminal", vm.SelectPaymentTerminalCommand)
            };
            foreach (var (name, command) in categories)
            {
                if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCommand)
                {
                    await asyncCommand.ExecuteAsync(null);
                }
                else
                {
                    command.Execute(null);
                }

                PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
                SaveScreenshot(view, $"settings-{name}-{theme}.png");
                failures.AddRange(CheckInputs(view).Select(item => $"{name}: {item}"));
                failures.AddRange(CheckIcons(view).Select(item => $"{name}: {item}"));
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            // 共享测试 Application 的其他用例依赖默认色板。
            applier.Apply(PosColorTheme.Default);
            view.DataContext = null;
            window.Content = null;
            window.Close();
            localization.SetCulture("en-US");
        }
    });

    private static IEnumerable<string> CheckInputs(FrameworkElement view)
    {
        foreach (var input in PaymentViewRuntimeStaTestHost.FindVisualDescendants<Control>(view)
                     .Where(control => control is TextBox or PasswordBox or ComboBox)
                     .Where(IsShown))
        {
            if (input.Foreground is not SolidColorBrush foreground)
            {
                continue;
            }

            var background = EffectiveBackground(input);
            var ratio = Contrast(foreground.Color, background);
            if (ratio < 4.5)
            {
                yield return $"{input.GetType().Name} text {foreground.Color} on {background} contrast {ratio:F2}";
            }
        }
    }

    private static IEnumerable<string> CheckIcons(FrameworkElement view)
    {
        foreach (var icon in PaymentViewRuntimeStaTestHost.FindVisualDescendants<PackIcon>(view).Where(IsShown))
        {
            if (icon.Foreground is not SolidColorBrush foreground || !icon.IsEnabled || EffectiveOpacity(icon) < 0.99)
            {
                continue;
            }

            var background = EffectiveBackground(VisualTreeHelper.GetParent(icon));
            var ratio = Contrast(foreground.Color, background);
            // 只拦“几乎看不见”：默认主题保留原有的琥珀色装饰图标（约 2.2），深色底上的黑图标只有 1.3 左右。
            if (ratio < 2)
            {
                yield return $"PackIcon {icon.Kind} {foreground.Color} on {background} contrast {ratio:F2}";
            }
        }
    }

    private static bool IsShown(UIElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return element.RenderSize.Width > 0 && element.RenderSize.Height > 0;
    }

    private static double EffectiveOpacity(UIElement element)
    {
        var opacity = 1.0;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui)
            {
                opacity *= ui.Opacity;
            }
        }

        return opacity;
    }

    // 从元素自身往上找第一块不透明的纯色底，作为文字/图标实际压在上面的颜色。
    private static Color EffectiveBackground(DependencyObject? start)
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            var brush = current switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                _ => null
            };
            if (brush is SolidColorBrush solid && solid.Color.A == 255 && solid.Opacity >= 0.99)
            {
                return solid.Color;
            }
        }

        return Colors.White;
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

        var l1 = Luminance(foreground);
        var l2 = Luminance(background);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
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
