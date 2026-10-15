using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsPaymentMethodsViewRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    [Theory]
    [InlineData(1366, 688, "en-US")]
    [InlineData(1366, 688, "zh-CN")]
    [InlineData(1024, 768, "en-US")]
    [InlineData(1024, 768, "zh-CN")]
    public Task Payment_method_switches_are_visible_touch_sized_and_save_independently(
        int width, int height, string culture) => host.RunAsync(async _ =>
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        LocalizationResourceProvider.Instance.Configure(localization);
        var methods = new MethodSettingsFake();
        var setup = DispatchProxy.Create<ICardTerminalSetupService, UnavailableTerminalSetup>();
        using var http = new HttpClient();
        var apiSettings = new ApiServerSettingsViewModel(
            new ApiServerSettingsService(http, () => "https://example.test/", _ => throw new NotSupportedException()),
            localization);
        using var vm = new SettingsViewModel(setup, localization,
            apiServerSettings: apiSettings, paymentMethodSettingsService: methods);
        await vm.LoadAsync();
        var setupCallsAfterLoad = ((UnavailableTerminalSetup)setup).Calls;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.SelectPaymentTerminalCommand).ExecuteAsync(null);
        var view = new SettingsView { DataContext = vm };
        view.Resources.MergedDictionaries.Add(new MaterialDesignThemes.Wpf.BundledTheme
        {
            BaseTheme = MaterialDesignThemes.Wpf.BaseTheme.Light,
            PrimaryColor = MaterialDesignColors.PrimaryColor.Blue,
            SecondaryColor = MaterialDesignColors.SecondaryColor.Amber
        });
        try
        {
            PaymentViewRuntimeStaTestHost.Realize(view, width, height);
            var manual = Assert.IsType<ToggleButton>(view.FindName("ManualCardSettingToggle"));
            var voucher = Assert.IsType<ToggleButton>(view.FindName("VoucherSettingToggle"));
            var orderList = Assert.IsType<ToggleButton>(view.FindName("OrderListSettingToggle"));
            var save = Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(view)
                .Where(button => ReferenceEquals(button.Command, vm.SavePaymentMethodsCommand)));
            AssertInBounds(view, manual, width, height);
            AssertInBounds(view, voucher, width, height);
            AssertInBounds(view, orderList, width, height);
            AssertInBounds(view, save, width, height);
            Assert.False(manual.IsChecked);
            Assert.False(voucher.IsChecked);
            // 付款页订单列表默认开启，且不受环境锁影响始终可切换。
            Assert.True(orderList.IsChecked);
            Assert.True(manual.IsEnabled);
            Assert.True(voucher.IsEnabled);
            Assert.True(orderList.IsEnabled);
            Assert.True(save.IsEnabled);
            var saveText = Assert.IsType<TextBlock>(save.Content);
            Assert.Equal(localization.T("settings.payment.methods.save"), saveText.Text);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(saveText.Foreground).Color);

            var output = Environment.GetEnvironmentVariable("HBPOS_PAYMENT_SETTINGS_SCREENSHOTS");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"payment-settings-{width}-{height}-{culture}.png"));
                encoder.Save(file);
            }

            manual.IsChecked = true;
            voucher.IsChecked = true;
            orderList.IsChecked = false;
            PaymentViewRuntimeStaTestHost.Realize(view, width, height);
            Assert.True(vm.UseManualCard);
            Assert.True(vm.VoucherEnabled);
            Assert.False(vm.ShowOrderList);
            Assert.Equal(new PaymentMethodSettings(), methods.Current);
            await vm.SavePaymentMethodsCommand.ExecuteAsync(null);
            Assert.Equal(new PaymentMethodSettings(true, true, false), methods.Current);
            Assert.Equal(1, methods.SaveCalls);
            // 修改付款方式只保存本地开关，不追加终端测试或配置写入。
            Assert.Equal(setupCallsAfterLoad, ((UnavailableTerminalSetup)setup).Calls);
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    [Fact]
    public Task Saved_payment_methods_load_before_terminal_configuration_failure() => host.RunAsync(async _ =>
    {
        var methods = new MethodSettingsFake { Current = new(true, true, false) };
        var setup = DispatchProxy.Create<ICardTerminalSetupService, UnavailableTerminalSetup>();
        ((UnavailableTerminalSetup)setup).FailLoad = true;
        using var vm = new SettingsViewModel(setup, paymentMethodSettingsService: methods);
        await vm.LoadAsync();
        Assert.True(vm.UseManualCard);
        Assert.True(vm.VoucherEnabled);
        Assert.False(vm.ShowOrderList);
        Assert.Equal("Terminal unavailable", vm.StatusMessage);
        Assert.True(vm.SavePaymentMethodsCommand.CanExecute(null));
    });

    private static void AssertInBounds(FrameworkElement view, FrameworkElement element, int width, int height)
    {
        // 离屏控件没有 PresentationSource，以祖先可见性和实际布局验证触摸区域。
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui) Assert.Equal(Visibility.Visible, ui.Visibility);
        }
        var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width + 1 && bounds.Bottom <= height + 1,
            $"{element.Name} outside viewport: {bounds}");
        Assert.True(element.ActualHeight >= 48, $"{element.Name} has touch height {element.ActualHeight}");
    }

    private sealed class MethodSettingsFake : IPaymentMethodSettingsService
    {
        public PaymentMethodSettings Current { get; set; } = new();
        public event EventHandler? Changed;
        public int SaveCalls { get; private set; }
        public Task<PaymentMethodSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task SaveAsync(PaymentMethodSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            Current = settings;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    public class UnavailableTerminalSetup : DispatchProxy
    {
        public int Calls { get; private set; }
        public bool FailLoad { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            if (!FailLoad && targetMethod?.Name == nameof(ICardTerminalSetupService.LoadConfigurationAsync))
                return Task.FromResult(CardTerminalConfiguration.Default);
            if (!FailLoad && targetMethod?.Name == nameof(ICardTerminalSetupService.LoadLinklyCloudCredentialAsync))
                return Task.FromResult(new LinklyCloudCredentialSettings(null, null, false));
            throw new IOException("Terminal unavailable");
        }
    }
}
