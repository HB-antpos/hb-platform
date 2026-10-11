using System.Reflection;
using System.Windows.Automation;
using System.Windows.Controls;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

/// <summary>支付终端设置页进入时默认显示当前启用的刷卡方式对应的服务商标签，手动切换后回写视图模型。</summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsProviderTabViewRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    [Theory]
    [InlineData(CardProcessorKind.Square, 0)]
    [InlineData(CardProcessorKind.Linkly, 1)]
    public Task Payment_settings_opens_on_the_tab_of_the_active_card_processor(
        CardProcessorKind processor,
        int expectedIndex) => host.RunAsync(async _ =>
    {
        var localization = new LocalizationService();
        LocalizationResourceProvider.Instance.Configure(localization);
        var setup = DispatchProxy.Create<ICardTerminalSetupService, ConfiguredTerminalSetup>();
        ((ConfiguredTerminalSetup)(object)setup).Processor = processor;
        using var vm = new SettingsViewModel(setup, localization);
        await vm.LoadAsync();

        var view = new SettingsView { DataContext = vm };
        PaymentViewRuntimeStaTestHost.Realize(view, 1366, 900);
        var tabs = Assert.Single(
            PaymentViewRuntimeStaTestHost.FindVisualDescendants<TabControl>(view)
                .Where(tab => AutomationProperties.GetAutomationId(tab) == "PaymentProviderTabs"));

        Assert.Equal(expectedIndex, tabs.SelectedIndex);

        // 手动切到另一家：回写视图模型，只是查看，不改变已启用的刷卡方式。
        tabs.SelectedIndex = 1 - expectedIndex;
        Assert.Equal(1 - expectedIndex, vm.SelectedProviderTabIndex);
    });

    public class ConfiguredTerminalSetup : DispatchProxy
    {
        public CardProcessorKind Processor { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ICardTerminalSetupService.LoadConfigurationAsync))
                return Task.FromResult(CardTerminalConfiguration.Default with { Processor = Processor });
            if (targetMethod?.Name == nameof(ICardTerminalSetupService.LoadLinklyCloudCredentialAsync))
                return Task.FromResult(new LinklyCloudCredentialSettings(null, null, false));
            throw new IOException("Terminal unavailable");
        }
    }
}