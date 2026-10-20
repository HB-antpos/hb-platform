using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class PaymentViewRuntimeTests
{
    private readonly PaymentViewRuntimeStaTestHost _staHost;

    public PaymentViewRuntimeTests(PaymentViewRuntimeStaTestHost staHost) => _staHost = staHost;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Card_exception_keeps_real_recovery_entry_clickable(bool persistenceFailure)
    {
        await _staHost.RunAsync(async _ =>
        {
            var openRecoveryCenterCalls = 0;
            using var viewModel = CreateViewModel(
                openCardRecoveryCenter: () => openRecoveryCenterCalls++,
                persistenceFailure: persistenceFailure);
            var view = new PaymentView
            {
                DataContext = viewModel
            };

            try
            {
                PaymentViewRuntimeStaTestHost.Realize(view);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                var openCenterButton = FindButton(view, "OpenCardRecoveryCenterFromCardErrorButton");
                Assert.False(IsEffectivelyVisible(openCenterButton));

                // 从真实付款命令进入异常状态；不反射私有会话或直接设置锁标志。
                await viewModel.SelectCardCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(viewModel.IsCardPaymentRecoveryRequired);
                Assert.True(viewModel.IsPaymentInteractionLocked);
                if (persistenceFailure)
                {
                    // 这是曾经锁死的组合：已有批准款项，但没有错误浮层。
                    Assert.Null(viewModel.CardPaymentErrorOverlay);
                    Assert.Equal(PaymentMethodKind.Card, Assert.Single(viewModel.PaymentTenders).Method);
                }

                PaymentViewRuntimeStaTestHost.Realize(view);
                Assert.True(IsEffectivelyVisible(openCenterButton));
                Assert.True(openCenterButton.IsEnabled);
                Assert.False(viewModel.BackToPosCommand.CanExecute(null));
                InvokeButton(openCenterButton);
                await PaymentViewRuntimeStaTestHost.WaitUntilAsync(
                    () => openRecoveryCenterCalls == 1, "恢复按钮没有触发实际导航命令。");
                Assert.True(viewModel.IsPaymentInteractionLocked);
            }
            finally
            {
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, view));
                view.DataContext = null;
            }
        });
    }

    [Fact]
    public async Task Empty_active_terminal_directory_keeps_refresh_entry_and_explicit_status()
    {
        await _staHost.RunAsync(async _ =>
        {
            var terminalSetup = CreateTerminalSetup();
            using var viewModel = CreateViewModel(cardTerminalSetupService: terminalSetup.Service);
            var view = new PaymentView
            {
                DataContext = viewModel
            };

            try
            {
                PaymentViewRuntimeStaTestHost.Realize(view);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                await PaymentViewRuntimeStaTestHost.WaitUntilAsync(
                    () => terminalSetup.Proxy.ListCalls == 1 &&
                        !viewModel.IsLinklyCloudTerminalRefreshing,
                    "PaymentView Loaded 未完成空终端目录刷新。");
                PaymentViewRuntimeStaTestHost.Realize(view);

                var refreshButton = Assert.Single(
                    PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(view)
                        .Where(button => string.Equals(
                            AutomationProperties.GetName(button),
                            "Refresh Linkly terminals",
                            StringComparison.Ordinal)));
                var terminalSelector = Assert.Single(
                    PaymentViewRuntimeStaTestHost.FindVisualDescendants<ComboBox>(view)
                        .Where(comboBox => string.Equals(
                            AutomationProperties.GetAutomationId(comboBox),
                            "PaymentLinklyCloudTerminalSelector",
                            StringComparison.Ordinal)));
                var statusText = Assert.Single(
                    PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(view)
                        .Where(textBlock => string.Equals(
                            textBlock.Text,
                            viewModel.LinklyCloudTerminalStatusText,
                            StringComparison.Ordinal)));

                // Active 但没有 Ready 终端时，目录区域和刷新入口仍需存在，不能把失败/空状态变成无提示空白。
                Assert.True(viewModel.IsLinklyCloudTerminalSelectorVisible);
                Assert.Empty(viewModel.LinklyCloudTerminals);
                Assert.Equal("No ready Linkly Cloud terminal is available.", viewModel.LinklyCloudTerminalStatusText);
                Assert.True(IsEffectivelyVisible(terminalSelector));
                Assert.True(IsEffectivelyVisible(refreshButton));
                Assert.True(IsEffectivelyVisible(statusText));
                Assert.True(refreshButton.IsEnabled);
                Assert.Contains("No ready Linkly Cloud terminal", statusText.Text, StringComparison.Ordinal);

                InvokeButton(refreshButton);
                await PaymentViewRuntimeStaTestHost.WaitUntilAsync(
                    () => terminalSetup.Proxy.ListCalls == 2 &&
                        !viewModel.IsLinklyCloudTerminalRefreshing,
                    "PaymentView 刷新按钮未再次请求终端目录。");

                Assert.Equal("No ready Linkly Cloud terminal is available.", viewModel.LinklyCloudTerminalStatusText);
            }
            finally
            {
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, view));
                view.DataContext = null;
            }
        });
    }

    private static PaymentViewModel CreateViewModel(
        Action? onBackToPos = null,
        Action? openCardRecoveryCenter = null,
        ICardTerminalSetupService? cardTerminalSetupService = null,
        bool persistenceFailure = false)
    {
        var cart = new PosCartService();
        cart.AddItem(new SellableItemDto("S01", "UI-CARD", null, "Card UI regression",
            "930UICARD", "UI-CARD", "930UICARD", 10m,
            PriceSourceKind.StoreRetailPrice, "StoreRetailPrice", 1m, null));
        return new PaymentViewModel(
            cart,
            new RuntimePaymentWorkflowFake { PersistenceFailure = persistenceFailure },
            new PosSessionState("HB POS", "S01", "Main", "POS-1", "C01", "Cashier", true, 0),
            localization: new LocalizationService(),
            onBackToPos: onBackToPos,
            openCardRecoveryCenter: openCardRecoveryCenter,
            cardTerminalSetupService: cardTerminalSetupService);
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        var provider = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        provider.Invoke();
    }

    private static bool IsEffectivelyVisible(DependencyObject element)
    {
        // 无可见桌面窗口的测试仍检查所有祖先，防止父区域折叠却只检查按钮自身。
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
                return false;
        }
        return true;
    }

    private static Button FindButton(PaymentView view, string automationId)
    {
        return Assert.Single(
            PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(view)
                .Where(button => string.Equals(
                    AutomationProperties.GetAutomationId(button),
                    automationId,
                    StringComparison.Ordinal)));
    }

    private static (ICardTerminalSetupService Service, RuntimeTerminalSetupFake Proxy) CreateTerminalSetup()
    {
        var service = DispatchProxy.Create<ICardTerminalSetupService, RuntimeTerminalSetupFake>();
        var proxy = (RuntimeTerminalSetupFake)(object)service;
        proxy.Configuration = CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
        };
        proxy.Directory = new LinklyCloudTerminalListResponse(
            "Sandbox",
            SelectedTerminalId: null,
            SelectionRevision: 7,
            Terminals: [],
            Mode: "Active");
        return (service, proxy);
    }

    private sealed class RuntimePaymentWorkflowFake : ICashPaymentWorkflowService
    {
        public bool PersistenceFailure { get; init; }

        public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount)
        {
            tenderedAmount = 0m;
            return false;
        }

        public decimal CalculateChange(string? amountTenderedText, decimal actualAmount) => 0m;

        public decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders) =>
            tenders.Sum(tender => tender.Amount);

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
            PosCartSnapshot? cartSnapshot = null) =>
            Task.FromResult(PersistenceFailure
                ? PaymentTenderAttemptResult.Success(
                    new PaymentTender(PaymentMethodKind.Card, 10m, "UI-APPROVED"), "payment.card.approved")
                : new PaymentTenderAttemptResult(false, "payment.card.resultUnknown",
                    StatusMessage: "Current card result is unknown.",
                    CardResult: new CardPaymentResultDisposition(
                        CardPaymentTerminalOutcome.ResultUnknown,
                        CardPaymentErrorKind.ActiveSessionRequiresRecovery, PreserveStatus: true),
                    RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly,
                        Guid.Parse("10000000-0000-0000-0000-000000000501")),
                    RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000501")));

        public Task<CashPaymentWorkflowResult> CompleteAsync(
            PosCartService cart,
            PosSessionState session,
            string? amountTenderedText,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CashPaymentWorkflowResult>(
                new NotSupportedException("PaymentView runtime test does not complete a payment."));

        public Task<CashPaymentWorkflowResult> CompletePaymentAsync(
            PosCartService cart,
            PosSessionState session,
            IReadOnlyList<PaymentTender> tenders,
            decimal cashTenderedAmount,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CashPaymentWorkflowResult>(
                new CardPaymentPersistenceUnknownException(
                    Guid.Parse("20000000-0000-0000-0000-000000000501"),
                    "Recover approved card after save failed.", new IOException("Simulated SQLite failure")));

        public Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
            Guid orderGuid,
            PosCartService cart,
            PosSessionState session,
            decimal tenderedAmount,
            decimal changeAmount,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CashPaymentWorkflowResult>(
                new NotSupportedException("PaymentView runtime test does not retry an upload."));
    }

    public class RuntimeTerminalSetupFake : DispatchProxy
    {
        public CardTerminalConfiguration Configuration { get; set; } = CardTerminalConfiguration.Default;

        public LinklyCloudTerminalListResponse Directory { get; set; } =
            new("Sandbox", null, null, [], "Active");

        public int ListCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                // 付款页改用不联网查令牌状态的读取方式；DispatchProxy 不会走接口默认实现，两个入口都要认。
                nameof(ICardTerminalSetupService.LoadConfigurationAsync) or
                nameof(ICardTerminalSetupService.LoadConfigurationWithoutTokenStatusAsync) =>
                    Task.FromResult(Configuration),
                nameof(ICardTerminalSetupService.ListLinklyCloudBackendTerminalsAsync) =>
                    ListDirectory(),
                _ => throw new NotSupportedException(
                    $"PaymentView terminal runtime test does not use {targetMethod?.Name}.")
            };
        }

        private Task<LinklyCloudTerminalListResponse> ListDirectory()
        {
            ListCalls++;
            return Task.FromResult(Directory);
        }
    }
}
