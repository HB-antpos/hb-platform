using System.Reflection;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class PaymentTerminalSelectionViewModelTests
{
    [Fact]
    public async Task RefreshLinklyCloudTerminalsAsync_shows_server_selected_ready_terminal()
    {
        var terminalId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var (setup, proxy) = CreateSetup(
            new LinklyCloudTerminalListResponse(
                "Sandbox",
                terminalId,
                9,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front Counter", "Ready", false, true, "Healthy", null)],
                "Active"));
        using var viewModel = CreateViewModel(setup);

        await viewModel.RefreshLinklyCloudTerminalsAsync();

        Assert.True(viewModel.IsLinklyCloudTerminalSelectorVisible);
        Assert.Equal(terminalId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Equal(9, viewModel.LinklyCloudSelectionRevision);
        Assert.Equal("Lane 1 · Front Counter", viewModel.SelectedLinklyCloudTerminalText);
        Assert.Equal(1, proxy.ListCalls);
    }

    [Fact]
    public async Task SelectLinklyCloudTerminalAsync_persists_expected_revision_for_next_payment()
    {
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        // Busy 仅阻止交易开始，不阻止其他 POS 预先选择同一台物理终端。
        var second = new LinklyCloudTerminalSummary(secondId, 2, "Side Counter", "Ready", true, true, "Healthy", null);
        var (setup, proxy) = CreateSetup(
            new LinklyCloudTerminalListResponse(
                "Sandbox",
                firstId,
                12,
                [
                    new LinklyCloudTerminalSummary(firstId, 1, "Front Counter", "Ready", false, true, "Healthy", null),
                    second
                ],
                "Active"));
        proxy.SelectionResult = new LinklyCloudTerminalSelectionResponse("Sandbox", secondId, 13);
        using var viewModel = CreateViewModel(setup);
        await viewModel.RefreshLinklyCloudTerminalsAsync();

        await viewModel.SelectLinklyCloudTerminalAsync(second);

        Assert.Equal(secondId, proxy.LastSelectedTerminalId);
        Assert.Equal(12, proxy.LastExpectedRevision);
        Assert.Equal(secondId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Equal(13, viewModel.LinklyCloudSelectionRevision);
    }

    [Fact]
    public async Task SelectLinklyCloudTerminalAsync_is_blocked_while_card_payment_is_in_progress()
    {
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var second = new LinklyCloudTerminalSummary(secondId, 2, "Side Counter", "Ready", false, true, "Healthy", null);
        var (setup, proxy) = CreateSetup(
            new LinklyCloudTerminalListResponse(
                "Sandbox",
                firstId,
                4,
                [
                    new LinklyCloudTerminalSummary(firstId, 1, "Front Counter", "Ready", false, true, "Healthy", null),
                    second
                ],
                "Active"));
        using var viewModel = CreateViewModel(setup);
        await viewModel.RefreshLinklyCloudTerminalsAsync();
        viewModel.IsCardPaymentInProgress = true;

        await viewModel.SelectLinklyCloudTerminalAsync(second);

        Assert.Null(proxy.LastSelectedTerminalId);
        Assert.Equal(firstId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);
    }

    [Fact]
    public async Task RefreshLinklyCloudTerminalsAsync_keeps_legacy_and_draft_on_existing_payment_flow()
    {
        var terminalId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        foreach (var mode in new[] { "Legacy", "Draft" })
        {
            var (setup, _) = CreateSetup(
                new LinklyCloudTerminalListResponse(
                    "Sandbox",
                    terminalId,
                    3,
                    [new LinklyCloudTerminalSummary(terminalId, 1, "Front Counter", "Ready", false, true, "Healthy", null)],
                    mode));
            using var viewModel = CreateViewModel(setup);

            await viewModel.RefreshLinklyCloudTerminalsAsync();

            Assert.False(viewModel.IsLinklyCloudTerminalSelectorVisible);
            Assert.Empty(viewModel.LinklyCloudTerminals);
            Assert.Null(viewModel.SelectedLinklyCloudTerminal);
        }
    }

    [Fact]
    public async Task RefreshLinklyCloudTerminalsAsync_keeps_empty_or_failed_active_directory_visible_and_retryable()
    {
        var (setup, proxy) = CreateSetup(
            new LinklyCloudTerminalListResponse(
                "Sandbox",
                null,
                3,
                [],
                "Active"));
        using var viewModel = CreateViewModel(setup);

        proxy.ListException = new TaskCanceledException("Linkly directory request timed out.");
        await viewModel.RefreshLinklyCloudTerminalsAsync();

        Assert.True(viewModel.IsLinklyCloudTerminalSelectorVisible);
        Assert.False(viewModel.CanSwitchLinklyCloudTerminal);
        Assert.Contains("timed out", viewModel.LinklyCloudTerminalStatusText, StringComparison.OrdinalIgnoreCase);

        proxy.ListException = null;
        await viewModel.RefreshLinklyCloudTerminalsAsync();

        Assert.True(viewModel.IsLinklyCloudTerminalSelectorVisible);
        Assert.Equal("No ready Linkly Cloud terminal is available.", viewModel.LinklyCloudTerminalStatusText);
        Assert.Equal(2, proxy.ListCalls);
    }

    private static PaymentViewModel CreateViewModel(ICardTerminalSetupService setup)
    {
        return new PaymentViewModel(
            new PosCartService(),
            new FakePaymentWorkflowService(),
            new PosSessionState("HB POS", "S01", "Main", "POS-1", "C01", "Cashier", true, 0),
            cardTerminalSetupService: setup);
    }

    private static (ICardTerminalSetupService Service, TerminalSetupProxy Proxy) CreateSetup(
        LinklyCloudTerminalListResponse directory)
    {
        var service = DispatchProxy.Create<ICardTerminalSetupService, TerminalSetupProxy>();
        var proxy = (TerminalSetupProxy)(object)service;
        proxy.Configuration = CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
        };
        proxy.Directory = directory;
        return (service, proxy);
    }

    private class TerminalSetupProxy : DispatchProxy
    {
        public CardTerminalConfiguration Configuration { get; set; } = CardTerminalConfiguration.Default;

        public LinklyCloudTerminalListResponse Directory { get; set; } = new("Sandbox", null, null, []);

        public LinklyCloudTerminalSelectionResponse SelectionResult { get; set; } =
            new("Sandbox", Guid.Empty, 1);

        public int ListCalls { get; private set; }

        public Exception? ListException { get; set; }

        public Guid? LastSelectedTerminalId { get; private set; }

        public long? LastExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                // 付款页改用不联网查令牌状态的读取方式；DispatchProxy 不会走接口默认实现，两个入口都要认。
                nameof(ICardTerminalSetupService.LoadConfigurationAsync) or
                nameof(ICardTerminalSetupService.LoadConfigurationWithoutTokenStatusAsync) => Task.FromResult(Configuration),
                nameof(ICardTerminalSetupService.ListLinklyCloudBackendTerminalsAsync) => List(),
                nameof(ICardTerminalSetupService.SelectLinklyCloudBackendTerminalAsync) => Select(args),
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
        }

        private Task<LinklyCloudTerminalListResponse> List()
        {
            ListCalls++;
            if (ListException is not null)
            {
                throw ListException;
            }

            return Task.FromResult(Directory);
        }

        private Task<LinklyCloudTerminalSelectionResponse> Select(object?[]? args)
        {
            LastSelectedTerminalId = (Guid?)args?[1];
            LastExpectedRevision = (long?)args?[2];
            return Task.FromResult(SelectionResult);
        }
    }

    private sealed class FakePaymentWorkflowService : ICashPaymentWorkflowService
    {
        public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount)
        {
            tenderedAmount = 0m;
            return false;
        }

        public decimal CalculateChange(string? amountTenderedText, decimal actualAmount) => 0m;

        public decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders) => tenders.Sum(item => item.Amount);

        public decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders) =>
            actualAmount - CalculateTenderedAmount(tenders);

        public decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount) => 0m;

        public Task<PaymentTenderAttemptResult> AddTenderAsync(
            PaymentMethodKind method,
            PosSessionState session,
            decimal actualAmount,
            IReadOnlyList<PaymentTender> currentTenders,
            string? amountText,
            string? referenceText = null,
            CancellationToken cancellationToken = default,
            PosCartSnapshot? cartSnapshot = null) => throw new NotSupportedException();

        public Task<CashPaymentWorkflowResult> CompleteAsync(
            PosCartService cart,
            PosSessionState session,
            string? amountTenderedText,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CashPaymentWorkflowResult> CompletePaymentAsync(
            PosCartService cart,
            PosSessionState session,
            IReadOnlyList<PaymentTender> tenders,
            decimal cashTenderedAmount,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
            Guid orderGuid,
            PosCartService cart,
            PosSessionState session,
            decimal tenderedAmount,
            decimal changeAmount,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
