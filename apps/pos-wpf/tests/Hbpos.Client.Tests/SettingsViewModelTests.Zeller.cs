using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

/// <summary>设置页的 Zeller 分页：检查 / 配对 / 启用的按钮状态、页签下标。协调器本身的测试在 ZellerSettingsTests。</summary>
public sealed partial class SettingsViewModelTests
{
    // ── 设置页 ──

    [Fact]
    public async Task Zeller_cannot_be_enabled_before_pairing_and_can_afterwards()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with { Processor = CardProcessorKind.Square });
        var gateway = new ZellerSettingsTests.ZellerPairingGateway { Initialise = new ZellerPairingResult(true) };
        var viewModel = CreateViewModel(service, ZellerCoordinatorFor(gateway, service));
        await viewModel.LoadAsync();

        Assert.False(viewModel.EnableZellerCommand.CanExecute(null));
        Assert.False(viewModel.IsZellerPaired);

        await viewModel.CheckZellerCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsZellerPaired);
        Assert.True(viewModel.EnableZellerCommand.CanExecute(null));
        Assert.Equal(0, service.SaveZellerCallCount);

        await viewModel.EnableZellerCommand.ExecuteAsync(null);

        Assert.Equal(1, service.SaveZellerCallCount);
        Assert.Equal(CardProcessorKind.Zeller, service.SavedConfiguration!.Processor);
        Assert.True(viewModel.IsZellerActive);
        Assert.False(viewModel.EnableZellerCommand.CanExecute(null));
        Assert.Contains("Zeller", viewModel.ActivePaymentProviderText);
    }

    [Fact]
    public async Task A_check_that_finds_no_pairing_leaves_enable_disabled_and_says_so()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default);
        var gateway = new ZellerSettingsTests.ZellerPairingGateway { Initialise = new ZellerPairingResult(false, ZellerErrorType.SetupRequired) };
        var viewModel = CreateViewModel(service, ZellerCoordinatorFor(gateway, service));
        await viewModel.LoadAsync();

        await viewModel.CheckZellerCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsZellerPaired);
        Assert.False(viewModel.EnableZellerCommand.CanExecute(null));
        Assert.True(viewModel.PairZellerCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pairing_a_terminal_allows_enabling_it()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default);
        var gateway = new ZellerSettingsTests.ZellerPairingGateway { OnSetup = () => Task.FromResult(new ZellerPairingResult(true)) };
        var viewModel = CreateViewModel(service, ZellerCoordinatorFor(gateway, service));
        await viewModel.LoadAsync();

        await viewModel.PairZellerCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsZellerPaired);
        Assert.False(viewModel.IsZellerPairingInProgress);
        Assert.True(viewModel.EnableZellerCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_a_pairing_in_progress_returns_to_not_paired()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default);
        var started = new TaskCompletionSource();
        var gateway = new ZellerSettingsTests.ZellerPairingGateway
        {
            OnSetupWithToken = async token =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                }

                return new ZellerPairingResult(false, ZellerErrorType.Cancelled);
            }
        };
        var viewModel = CreateViewModel(service, ZellerCoordinatorFor(gateway, service));
        await viewModel.LoadAsync();

        var pairing = viewModel.PairZellerCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(viewModel.IsZellerPairingInProgress);
        Assert.True(viewModel.CancelZellerPairingCommand.CanExecute(null));
        Assert.False(viewModel.PairZellerCommand.CanExecute(null));

        viewModel.CancelZellerPairingCommand.Execute(null);
        await pairing;

        Assert.False(viewModel.IsZellerPairingInProgress);
        Assert.False(viewModel.IsZellerPaired);
        Assert.False(viewModel.CancelZellerPairingCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_a_coordinator_the_zeller_buttons_are_unavailable()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(CardTerminalConfiguration.Default));
        await viewModel.LoadAsync();

        Assert.False(viewModel.IsZellerAvailable);
        Assert.False(viewModel.CheckZellerCommand.CanExecute(null));
        Assert.False(viewModel.PairZellerCommand.CanExecute(null));
        Assert.False(viewModel.EnableZellerCommand.CanExecute(null));
    }

    [Fact]
    public async Task Selecting_the_zeller_sub_tab_selects_provider_tab_two_and_back()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(CardTerminalConfiguration.Default));
        await viewModel.LoadAsync();

        viewModel.IsZellerSubTabSelected = true;
        Assert.Equal(2, viewModel.SelectedProviderTabIndex);
        Assert.True(viewModel.IsProviderSubTabSelected);
        Assert.False(viewModel.IsSquareSubTabSelected);
        Assert.False(viewModel.IsLinklySubTabSelected);

        viewModel.SelectedProviderTabIndex = 1;
        Assert.True(viewModel.IsLinklySubTabSelected);
        Assert.False(viewModel.IsZellerSubTabSelected);

        viewModel.SelectedProviderTabIndex = 2;
        Assert.True(viewModel.IsZellerSubTabSelected);
    }

    [Theory]
    [InlineData(CardProcessorKind.None, 0)]
    [InlineData(CardProcessorKind.Square, 0)]
    [InlineData(CardProcessorKind.Linkly, 1)]
    [InlineData(CardProcessorKind.Zeller, 2)]
    public void Provider_tab_index_follows_the_processor(CardProcessorKind processor, int expected)
    {
        Assert.Equal(expected, SettingsViewModel.ProviderTabIndexFor(processor));
        Assert.Equal(SettingsViewModel.SubTabForProviderIndex(expected), SettingsViewModel.SubTabForProviderIndex(SettingsViewModel.ProviderTabIndexFor(processor)));
    }

    /// <summary>设置页视图测试用：协调器存在但从不会被点到（这些测试只看布局）。</summary>
    internal sealed class NeverAskedGateway : IZellerTerminalGateway
    {
        public Task<ZellerPairingResult> InitialiseAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ZellerPairingResult(true));

        public Task<ZellerPairingResult> SetupAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ZellerPairingResult(true));

        public Task<ZellerCallResult> PurchaseAsync(ZellerPurchaseRequest request, IProgress<ZellerTerminalEvent>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ZellerCallResult> RefundAsync(ZellerRefundRequest request, IProgress<ZellerTerminalEvent>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ZellerQueryResult> GetTransactionsAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ZellerSettingsCoordinator ZellerCoordinatorFor(ZellerSettingsTests.ZellerPairingGateway gateway, FakeCardTerminalSetupService service) =>
        new(gateway, (configuration, cancellationToken) => service.SaveZellerAsync(configuration, cancellationToken));

    private static SettingsViewModel CreateViewModel(FakeCardTerminalSetupService service, ZellerSettingsCoordinator coordinator) =>
        new(service, zellerSettingsCoordinator: coordinator);
}
