using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class CardRecoveryCenterViewRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    [Theory]
    [InlineData(1024, 768, "zh-CN")]
    [InlineData(1366, 768, "en-US")]
    [InlineData(1707, 1019, "en-US")]
    [InlineData(1707, 1019, "zh-CN")]
    public Task Recovery_center_renders_touch_actions_inside_the_screen_in_the_blue_theme(int width, int height, string culture) =>
        host.RunAsync(async _ =>
        {
            var localization = new LocalizationService();
            localization.SetCulture(culture);
            LocalizationResourceProvider.Instance.Configure(localization);
            var applier = new WpfColorThemeApplier();
            applier.Apply(PosColorTheme.Blue);
            try
            {
                var squareRefund = CreateItem(CardProcessorKind.Square, "Refund", 0.50m, "Recovering", Now.AddMinutes(-2)) with
                {
                    PaymentId = "GUVaMb5MDdsqHjjkzHEeOglJhhGZY_DDZaNz1NMp4xdSt38IgcBHobLbe7VhbX3kQOFLhAaGR",
                    PaymentStatus = "PENDING",
                    OrderDraftJson = """{"orderGuid":"89bfe0a9-0720-4028-ba30-64f8a4a0bfea","session":{"cashierName":"storemanager"},"cartSnapshot":{"lines":[{"storeCode":"1042","productCode":"G006243","displayName":"OPEN ITEM","lookupCode":"OPENITEM","itemNumber":"OPEN ITEM","quantity":1,"unitPrice":0.50,"discountAmount":0,"priceSource":0,"priceSourceLabel":"Receipt return","kind":1}]}}"""
                };
                var linklySale = CreateItem(CardProcessorKind.Linkly, "Sale", 23.40m, "RequiresReview", Now.AddMinutes(-14));
                var squareSale = CreateItem(CardProcessorKind.Square, "Sale", 12.00m, "Recovering", Now.AddHours(-1));
                using var viewModel = new CardRecoveryCenterViewModel(
                    new FakeRecoveryService([squareRefund, linklySale, squareSale]),
                    new PosCartService(),
                    CreateSession(),
                    new AllowingAuthorizationService(),
                    localization);
                await viewModel.LoadAsync();
                var view = new CardRecoveryCenterView { DataContext = viewModel };
                PaymentViewRuntimeStaTestHost.Realize(view, width, height);
                PaymentViewRuntimeStaTestHost.Realize(view, width, height);

                Assert.Equal(squareRefund.Key, viewModel.SelectedAttempt?.Key);
                AssertCommandButtonOnScreen(view, viewModel.RecoverCommand, width, height);
                AssertCommandButtonOnScreen(view, viewModel.BackCommand, width, height);
                AssertCommandButtonOnScreen(view, viewModel.OpenManualCommand, width, height);
                SaveScreenshot(view, width, height, $"overview-{culture}");

                // 人工核对面板：选中允许主管结案的交易后打开，三个结果卡片都要在屏幕内可点。
                viewModel.SelectedRow = viewModel.OpenAttemptRows.Single(row => row.Key == linklySale.Key);
                viewModel.IsManualExpanded = true;
                PaymentViewRuntimeStaTestHost.Realize(view, width, height);
                PaymentViewRuntimeStaTestHost.Realize(view, width, height);
                foreach (var command in new[] { viewModel.ConfirmPaidCommand, viewModel.ConfirmNotPaidCommand, viewModel.ContinueWaitingCommand })
                {
                    var button = FindCommandButton(view, command);
                    Assert.True(button.ActualHeight >= 48, "结果卡片应保持触屏高度。");
                    Assert.True(button.ActualWidth > 120, $"结果卡片过窄：{button.ActualWidth}");
                }

                SaveScreenshot(view, width, height, $"manual-{culture}");
            }
            finally
            {
                // 共享测试 Application 的其他用例依赖默认色板。
                applier.Apply(PosColorTheme.Default);
            }
        });

    private static Button FindCommandButton(DependencyObject view, ICommand command) =>
        Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(view)
            .Where(button => ReferenceEquals(button.Command, command) && button.ActualHeight > 0));

    private static void AssertCommandButtonOnScreen(FrameworkElement view, ICommand command, int width, int height)
    {
        var button = FindCommandButton(view, command);
        var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
        Assert.True(
            bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width + 0.5 && bounds.Bottom <= height + 0.5,
            $"按钮 {button.Content} 超出屏幕：{bounds}");
        Assert.True(bounds.Height >= 48, $"按钮 {button.Content} 触屏高度不足：{bounds.Height}");
    }

    private static void SaveScreenshot(FrameworkElement view, int width, int height, string name)
    {
        var output = Environment.GetEnvironmentVariable("HBPOS_CARD_RECOVERY_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, $"card-recovery-{name}-{width}x{height}.png"));
        encoder.Save(stream);
    }

    private static CardRecoveryQueueItem CreateItem(
        CardProcessorKind processor,
        string operationKind,
        decimal amount,
        string status,
        DateTimeOffset updatedAt) =>
        new(
            processor,
            Guid.NewGuid(),
            operationKind,
            amount,
            "1042",
            "POS_1042_2007",
            "22ada0d91ad74b4d93561122a83645e0",
            "Production",
            status,
            updatedAt.AddSeconds(-20),
            updatedAt,
            OrderDraftJson: $$"""{"orderGuid":"{{Guid.NewGuid()}}"}""",
            TxnRef: processor == CardProcessorKind.Linkly ? "TXN-000123" : null,
            ResponseText: "Card terminal result could not be confirmed. Recovery is required.");

    private static PosSessionState CreateSession()
    {
        var cashier = new CashierSessionDto(
            "22ada0d91ad74b4d93561122a83645e0",
            "USER-1",
            "storemanager",
            "1042",
            "POS_1042_2007",
            [],
            [Permissions.PosTerminal.Payment.View],
            ["1042"],
            IsSuperAdmin: false,
            IsOfflineCached: false,
            IsEmergencyOverride: false);
        return new PosSessionState("HB POS", "1042", "TestStore", "POS_1042_2007", cashier.CashierId, cashier.CashierName, true, 0, cashier);
    }

    private sealed class FakeRecoveryService(IReadOnlyList<CardRecoveryQueueItem> items)
        : ICardPaymentRecoveryService, ICardRecoveryQueueLoader
    {
        public Task<CardRecoveryQueueLoadResult> LoadOpenQueueAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CardRecoveryQueueLoadResult(items, []));

        public Task<CardPaymentRecoveryResult> RecoverLatestAsync(
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CardPaymentRecoveryResult.None);

        public Task<CardPaymentRecoveryResult> RecoverActiveSessionAsync(
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CardPaymentRecoveryResult.None);

        public Task<CardPaymentRecoveryResult> ManuallyClearActiveSessionAsync(
            string sessionId,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CardPaymentRecoveryResult.None);
    }

    private sealed class AllowingAuthorizationService : IOperationAuthorizationService
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public string ScannerPageId => "card-recovery-center-runtime-test";
        public bool IsPromptOpen => false;
        public bool IsBusy => false;
        public string PromptMessage => string.Empty;
        public string StatusMessage => string.Empty;
        public string PermissionCode => string.Empty;
        public string Screen => string.Empty;
        public string Action => string.Empty;
        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<OperationAuthorizationScope?>(
                session.CashierSession is null
                    ? null
                    : new OperationAuthorizationScope(session.CashierSession, permissionCode, screen, action));

        public bool ProcessScannerBarcode(string barcode) => false;
        public void Cancel() { }
        public void RevokeAll() { }
    }
}
