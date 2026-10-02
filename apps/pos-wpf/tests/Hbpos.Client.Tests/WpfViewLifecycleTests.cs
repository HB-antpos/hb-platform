using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfViewLifecycleTestCollection : ICollectionFixture<PaymentViewRuntimeStaTestHost>
{
    public const string Name = nameof(WpfViewLifecycleTestCollection);
}

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class WpfViewLifecycleTests
{
    private readonly PaymentViewRuntimeStaTestHost _staHost;

    public WpfViewLifecycleTests(PaymentViewRuntimeStaTestHost staHost)
    {
        _staHost = staHost;
    }

    [Fact]
    public async Task Views_subscribe_only_while_loaded_and_loaded_cycles_are_idempotent()
    {
        await _staHost.RunAsync(_ =>
        {
            VerifySettingsViewLifecycle();
            VerifyLinklyLineCardsLayout();
            VerifyTransactionHistoryViewLifecycle();
            VerifyTransactionHistoryOrderDetailsLayout();
            VerifyTransactionHistoryDisabledDetailsReason();
            VerifyPaymentViewLifecycle();
            VerifyDailyCloseCashCountDialogBindings();
            VerifyDailyCloseCashWorkspaceRuntime();
            VerifyPosTerminalTouchLayout();
            VerifyUnloadedViewInstancesAreCollectible();
            return Task.CompletedTask;
        });
    }

    private static void VerifyLinklyLineCardsLayout()
    {
        var localization = new LocalizationService();
        LocalizationResourceProvider.Instance.Configure(localization);
        using var model = new SettingsViewModel(
            DispatchProxy.Create<ICardTerminalSetupService, ThrowingDispatchProxy>(), localization);
        model.SelectedCategory = SettingsCategory.PaymentTerminal;
        model.SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync;
        var rows = new List<Hbpos.Client.Wpf.ViewModels.Settings.LinklyCloudLineItem>();
        for (var i = 1; i <= 3; i++)
        {
            var terminal = new Hbpos.Contracts.Linkly.LinklyCloudTerminalSummary(Guid.NewGuid(), i,
                i == 2 ? "Customer service / 客服收银台 B" : $"Checkout {i}", "Ready", false, true, null, null,
                i == 1 ? "POS-1" : null, i, $"v-{i}");
            var devices = new[]
            {
                new Hbpos.Contracts.Linkly.LinklyCloudAssignableDevice("POS-1", "WPF", true, i == 1 ? terminal.TerminalId : null, i),
                new Hbpos.Contracts.Linkly.LinklyCloudAssignableDevice("IPAD-2", "iPad", true, null, i)
            };
            var managementItem = new LinklyCloudTerminalManagementItem(
                terminal, devices, devices[0], localize: localization.T);
            rows.Add(new Hbpos.Client.Wpf.ViewModels.Settings.LinklyCloudLineItem(
                terminal,
                localization.T, () => false, _ => { }, _ => { }, _ => { }, _ => { },
                _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, managementItem, supportsManagement: true));
            model.LinklyCloudLines.Add(rows[^1]);
        }
        rows[1].IsExpanded = true;
        rows[1].IsEnteringCode = true;
        rows[1].PairCode = "386214";
        var view = new SettingsView { DataContext = model };
        try
        {
            view.Measure(new Size(1366, 5000));
            view.Arrange(new Rect(0, 0, 1366, 5000));
            view.UpdateLayout();
            Assert.Single(FindVisualDescendants<TabControl>(view)).SelectedIndex = 1;
            foreach (var culture in new[] { "en-US", "zh-CN" })
            {
                localization.SetCulture(culture);
                foreach (var width in new[] { 1366d, 1024d })
                {
                    view.Measure(new Size(width, 5000));
                    view.Arrange(new Rect(0, 0, width, 5000));
                    view.UpdateLayout();
                    var cards = Assert.Single(FindVisualDescendants<ItemsControl>(view).Where(item =>
                        AutomationProperties.GetAutomationId(item) == "LinklyCloudLineCards"));
                    Assert.Empty(FindVisualDescendants<ItemsControl>(view).Where(item =>
                        AutomationProperties.GetAutomationId(item) == "LinklyCloudTerminalLineList"));
                    var toggles = FindVisualDescendants<Button>(cards).Where(button =>
                        AutomationProperties.GetAutomationId(button) == "LinklyLineTogglePairing").ToArray();
                    Assert.Equal(3, toggles.Length);
                    Assert.Equal(3, FindVisualDescendants<Button>(cards).Count(button =>
                        AutomationProperties.GetAutomationId(button) == "LinklyLineUseForPayments"));
                    Assert.All(toggles, button => Assert.True(button.ActualHeight >= 44));
                    Assert.All(toggles, button => Assert.NotNull(button.Command));
                    Assert.Equal(localization.T("settings.linkly.lines.repair"), Assert.IsType<TextBlock>(toggles[0].Content).Text);
                    Assert.Equal(localization.T("settings.linkly.lines.collapsePairing"), Assert.IsType<TextBlock>(toggles[1].Content).Text);
                    var input = Assert.Single(FindVisualDescendants<TextBox>(cards).Where(box => box.ActualHeight > 0));
                    Assert.Equal("386214", input.Text);
                    Assert.Equal(6, input.MaxLength);
                    AssertHorizontallyContained(input, cards, width, 5000);
                    foreach (var button in FindVisualDescendants<Button>(cards).Where(button => button.ActualWidth > 0))
                        AssertHorizontallyContained(button, cards, width, 5000);
                    foreach (var target in FindVisualDescendants<ComboBox>(cards).Where(combo => combo.ActualWidth > 0))
                        AssertHorizontallyContained(target, cards, width, 5000);
                    Assert.All(FindVisualDescendants<TextBlock>(cards), text => Assert.DoesNotContain("[[settings.", text.Text));
                    SaveLinklyLayoutEvidence(cards, $"linkly-lines-{culture}-{width:0}-entry.png");
                }
            }
            rows[1].IsEnteringCode = false;
            rows[1].IsConfirming = true;
            view.UpdateLayout();
            var confirmedCards = Assert.Single(FindVisualDescendants<ItemsControl>(view).Where(item =>
                AutomationProperties.GetAutomationId(item) == "LinklyCloudLineCards"));
            var confirmation = Assert.Single(FindVisualDescendants<Button>(confirmedCards).Where(button =>
                AutomationProperties.GetAutomationId(button) == "LinklyLineConfirmPairing" && button.ActualHeight > 0));
            Assert.Same(rows[1].ConfirmCommand, confirmation.Command);
            SaveLinklyLayoutEvidence(confirmedCards, "linkly-lines-zh-CN-confirm.png");
            RaiseLoaded(view);
            RaiseUnloaded(view);
            Assert.All(rows, row => { Assert.False(row.IsExpanded); Assert.Empty(row.PairCode); });
            rows[0].IsExpanded = true;
            rows[0].PairCode = "654321";
            RaiseLoaded(view);
            view.DataContext = null;
            Assert.False(rows[0].IsExpanded);
            Assert.Empty(rows[0].PairCode);
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    }

    private static void SaveLinklyLayoutEvidence(FrameworkElement element, string fileName)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("HBPOS_TEST_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        System.IO.Directory.CreateDirectory(outputDirectory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        // VisualBrush 从卡片自身原点绘制，避免布局偏移占用画布并裁掉底部。
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            context.DrawRectangle(Brushes.White, null, bounds);
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(System.IO.Path.Combine(outputDirectory, fileName));
        encoder.Save(output);
    }

    private static void VerifySettingsViewLifecycle()
    {
        using var first = CreateSettingsViewModel();
        using var second = CreateSettingsViewModel();
        var view = new SettingsView { DataContext = first };

        AssertDirectHandlerCount(first, view, "ViewModel_PropertyChanged", 0);
        RaiseLoaded(view);
        AssertDirectHandlerCount(first, view, "ViewModel_PropertyChanged", 1);
        RaiseLoaded(view);
        AssertDirectHandlerCount(first, view, "ViewModel_PropertyChanged", 1);

        view.DataContext = second;
        AssertDirectHandlerCount(first, view, "ViewModel_PropertyChanged", 0);
        AssertDirectHandlerCount(second, view, "ViewModel_PropertyChanged", 1);

        RaiseUnloaded(view);
        AssertDirectHandlerCount(second, view, "ViewModel_PropertyChanged", 0);
        Assert.Null(ReadPrivateField(view, "_viewModel"));
        RaiseUnloaded(view);
        AssertDirectHandlerCount(second, view, "ViewModel_PropertyChanged", 0);

        RaiseLoaded(view);
        AssertDirectHandlerCount(second, view, "ViewModel_PropertyChanged", 1);
        RaiseUnloaded(view);
        view.DataContext = null;
    }

    private static void VerifyTransactionHistoryViewLifecycle()
    {
        using var first = new TransactionHistoryViewModel();
        using var second = new TransactionHistoryViewModel();
        var view = new TransactionHistoryView { DataContext = first };
        VerifyTransactionHistorySearchClearButton(view, first);

        AssertDirectHandlerCount(first, view, "ViewModelPropertyChanged", 0);
        RaiseLoaded(view);
        AssertDirectHandlerCount(first, view, "ViewModelPropertyChanged", 1);
        RaiseLoaded(view);
        AssertDirectHandlerCount(first, view, "ViewModelPropertyChanged", 1);

        first.IsHeldSourceSelected = true;
        var generationBeforeShown = Assert.IsType<long>(ReadPrivateField(first, "_heldLoadGeneration"));
        InvokePrivateMethod(view, "ShowAttachedViewModel");
        var generationAfterFirstShown = Assert.IsType<long>(ReadPrivateField(first, "_heldLoadGeneration"));
        InvokePrivateMethod(view, "ShowAttachedViewModel");
        Assert.Equal(generationBeforeShown + 1, generationAfterFirstShown);
        Assert.Equal(generationAfterFirstShown, Assert.IsType<long>(ReadPrivateField(first, "_heldLoadGeneration")));

        view.DataContext = second;
        AssertDirectHandlerCount(first, view, "ViewModelPropertyChanged", 0);
        AssertDirectHandlerCount(second, view, "ViewModelPropertyChanged", 1);
        Assert.False(Assert.IsType<bool>(ReadPrivateField(first, "_isScreenVisible")));

        InvokePrivateMethod(view, "ShowAttachedViewModel");
        Assert.True(Assert.IsType<bool>(ReadPrivateField(second, "_isScreenVisible")));

        RaiseUnloaded(view);
        AssertDirectHandlerCount(second, view, "ViewModelPropertyChanged", 0);
        Assert.Null(ReadPrivateField(view, "_viewModel"));
        Assert.False(Assert.IsType<bool>(ReadPrivateField(view, "_isScreenShown")));
        Assert.False(Assert.IsType<bool>(ReadPrivateField(second, "_isScreenVisible")));
        RaiseUnloaded(view);
        AssertDirectHandlerCount(second, view, "ViewModelPropertyChanged", 0);

        RaiseLoaded(view);
        AssertDirectHandlerCount(second, view, "ViewModelPropertyChanged", 1);
        RaiseUnloaded(view);
        view.DataContext = null;
    }

    private static void VerifyTransactionHistorySearchClearButton(
        TransactionHistoryView view,
        TransactionHistoryViewModel viewModel)
    {
        var searchTextBox = Assert.IsType<TextBox>(view.FindName("HistorySearchTextBox"));
        var clearButton = Assert.IsType<Button>(view.FindName("HistorySearchClearButton"));

        PumpDispatcher();
        Assert.Equal(Visibility.Collapsed, clearButton.Visibility);

        viewModel.SearchText = "order-123";
        PumpDispatcher();
        Assert.Equal("order-123", searchTextBox.Text);
        Assert.Equal(Visibility.Visible, clearButton.Visibility);

        clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpDispatcher();
        Assert.Equal(string.Empty, searchTextBox.Text);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(Visibility.Collapsed, clearButton.Visibility);
    }

    private static void VerifyTransactionHistoryOrderDetailsLayout()
    {
        VerifyTransactionHistoryOrderDetailsLayoutAtSize(1080, 720);
        VerifyTransactionHistoryOrderDetailsLayoutAtSize(1366, 768);
    }

    private static void VerifyTransactionHistoryDisabledDetailsReason()
    {
        var localization = new LocalizationService();
        using var viewModel = new TransactionHistoryViewModel();
        var remoteHeldOrder = new HistoryOrderListItem(
            Guid.NewGuid(),
            TransactionHistorySource.HeldOrders,
            "S001",
            "POS-02",
            "Bob",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            1,
            "Suspended",
            "Remote pending",
            IsHeldOrder: true,
            IsSuspendedOrder: false,
            CanRemoteRecall: true);
        viewModel.Orders.Add(remoteHeldOrder);
        viewModel.SelectedOrder = remoteHeldOrder;
        var view = new TransactionHistoryView { DataContext = viewModel };
        var host = new Window
        {
            Content = view,
            Width = 1080,
            Height = 720,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false
        };

        host.Show();
        host.UpdateLayout();
        try
        {
            var historyGrid = Assert.IsType<DataGrid>(view.FindName("HistoryOrdersGrid"));
            var historyRow = Assert.IsType<DataGridRow>(historyGrid.ItemContainerGenerator.ContainerFromIndex(0));
            var detailsButton = Assert.Single(FindVisualDescendants<Button>(historyRow).Where(button =>
                AutomationProperties.GetAutomationId(button) == "TransactionHistoryOrderDetailsButton"));
            var expectedReason = localization.T("history.remoteHeldDetailsUnavailable");

            Assert.False(detailsButton.IsEnabled);
            Assert.Equal(expectedReason, AutomationProperties.GetHelpText(detailsButton));

            var toolTip = Assert.IsType<ToolTip>(detailsButton.ToolTip);
            toolTip.PlacementTarget = detailsButton;
            toolTip.IsOpen = true;
            PumpDispatcher();
            Assert.Equal(expectedReason, toolTip.Content);
            toolTip.IsOpen = false;
        }
        finally
        {
            host.Close();
            view.DataContext = null;
        }
    }

    private static void VerifyTransactionHistoryOrderDetailsLayoutAtSize(
        double targetWidth,
        double targetHeight)
    {
        var orderGuid = Guid.NewGuid();
        var soldAt = new DateTimeOffset(2026, 8, 26, 9, 30, 0, TimeSpan.Zero);
        using var viewModel = new TransactionHistoryViewModel();
        var order = new HistoryOrderListItem(
            orderGuid,
            TransactionHistorySource.LocalOrders,
            "S001",
            "POS-01",
            "Alice",
            soldAt,
            28m,
            2m,
            26m,
            4,
            "Cash",
            "Synced",
            CanRecall: true,
            CanShare: true);
        viewModel.Orders.Add(order);
        viewModel.SelectedOrder = order;
        viewModel.SelectedReceipt = new ReceiptDetails(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            soldAt,
            28m,
            2m,
            26m,
            Enumerable.Range(1, 4)
                .Select(index => new ReceiptPreviewLine(
                    $"History product {index}",
                    $"93000000000{index}",
                    1m,
                    7m,
                    index == 4 ? 2m : 0m,
                    index == 4 ? 5m : 7m)
                {
                    ProductCode = $"P-{index:000}",
                    ItemNumber = $"ITEM-{index:000}"
                })
                .ToArray(),
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 30m, null)],
            TenderedAmount: 30m,
            ChangeAmount: 4m,
            OrderDisplay: "ORDER-LAYOUT-001");
        viewModel.PreviewSubtotal = 28m;
        viewModel.PreviewDiscount = 2m;
        viewModel.PreviewTotal = 26m;

        var view = new TransactionHistoryView { DataContext = viewModel };
        var host = new Window
        {
            Content = view,
            Width = targetWidth,
            Height = targetHeight,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false
        };

        host.Show();
        host.Activate();
        viewModel.OpenOrderDetailsCommand.Execute(order);
        PumpDispatcher();
        host.UpdateLayout();
        view.UpdateLayout();

        try
        {
            var overlay = Assert.IsType<UserControl>(view.FindName("OrderDetailsOverlay"));
            var dialog = Assert.IsType<Border>(view.FindName("OrderDetailsDialog"));
            var closeButton = Assert.IsType<Button>(view.FindName("OrderDetailsCloseButton"));
            var itemsGrid = Assert.IsType<DataGrid>(view.FindName("OrderDetailsItemsGrid"));
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(overlay));
            Assert.True(AutomationProperties.GetIsDialog(overlay));
            Assert.NotNull(UIElementAutomationPeer.CreatePeerForElement(overlay));
            Assert.InRange(dialog.ActualWidth, 999.5, 1000.5);
            Assert.InRange(dialog.ActualHeight, targetHeight - 48.5, targetHeight - 47.5);
            AssertFullyContained(dialog, overlay);
            AssertFullyContained(closeButton, dialog);
            Assert.Same(closeButton, Keyboard.FocusedElement);

            Assert.Equal(5, itemsGrid.Columns.Count);
            Assert.True(
                itemsGrid.Columns.Sum(column => column.ActualWidth) <= itemsGrid.ActualWidth + 0.5,
                $"{targetWidth:0}×{targetHeight:0} 下订单明细列宽超出可视区域。");
            var scrollViewer = Assert.Single(FindVisualDescendants<ScrollViewer>(itemsGrid));
            Assert.Equal(Visibility.Collapsed, scrollViewer.ComputedHorizontalScrollBarVisibility);

            var detailRow = Assert.IsType<DataGridRow>(itemsGrid.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.InRange(detailRow.ActualHeight, 75.5, 76.5);
            var productImage = Assert.Single(FindVisualDescendants<Border>(detailRow).Where(border =>
                border.Name == "OrderDetailProductImage"));
            Assert.InRange(productImage.ActualWidth, 63.5, 64.5);
            Assert.InRange(productImage.ActualHeight, 63.5, 64.5);
            AssertFullyContained(productImage, detailRow);

            var historyGrid = Assert.IsType<DataGrid>(view.FindName("HistoryOrdersGrid"));
            var visibleHistoryColumns = historyGrid.Columns
                .Where(column => column.Visibility == Visibility.Visible)
                .ToArray();
            Assert.True(
                visibleHistoryColumns.Sum(column => column.ActualWidth) <= historyGrid.ActualWidth + 0.5,
                $"{targetWidth:0}×{targetHeight:0} 下历史订单列宽超出可视区域：" +
                $"列宽 [{string.Join(", ", visibleHistoryColumns.Select(column => column.ActualWidth.ToString("0.##")))}]，" +
                $"表格 {historyGrid.ActualWidth:0.##}。");
            var historyRow = Assert.IsType<DataGridRow>(historyGrid.ItemContainerGenerator.ContainerFromIndex(0));
            var detailsButton = Assert.Single(FindVisualDescendants<Button>(historyRow).Where(button =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(button) ==
                "TransactionHistoryOrderDetailsButton"));
            Assert.InRange(detailsButton.ActualWidth, 43.5, 44.5);
            Assert.InRange(detailsButton.ActualHeight, 43.5, 44.5);
            AssertFullyContained(detailsButton, historyRow);

            var closeButtons = FindVisualDescendants<Button>(dialog)
                .Where(button => ReferenceEquals(button.Command, viewModel.CloseOrderDetailsCommand))
                .ToArray();
            Assert.Equal(2, closeButtons.Length);
            Assert.All(closeButtons, button => AssertFullyContained(button, dialog));

            RaiseEscapeFromFocusedElement();
            Assert.False(viewModel.IsOrderDetailsOpen);
        }
        finally
        {
            host.Close();
            view.DataContext = null;
        }
    }

    private static void VerifyPaymentViewLifecycle()
    {
        var second = new CountingPropertyChangedSource();
        var view = new PaymentView();
        var releasedViewModel = LoadAndReplacePaymentViewModel(view, second);
        AssertDirectHandlerCount(second, view, "PaymentViewModelPropertyChanged", 1);
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        ForceFullCollection();
        Assert.False(releasedViewModel.IsAlive, "切换 DataContext 后旧 Payment VM 仍被视图持有。");

        RaiseUnloaded(view);
        AssertDirectHandlerCount(second, view, "PaymentViewModelPropertyChanged", 0);
        Assert.Null(ReadPrivateField(view, "_viewModelNotifications"));
        RaiseUnloaded(view);
        AssertDirectHandlerCount(second, view, "PaymentViewModelPropertyChanged", 0);

        RaiseLoaded(view);
        AssertDirectHandlerCount(second, view, "PaymentViewModelPropertyChanged", 1);
        RaiseUnloaded(view);
        view.DataContext = null;
    }

    private static void VerifyDailyCloseCashCountDialogBindings()
    {
        var view = new DailyCloseView
        {
            DataContext = new CashCountDialogBindingSource()
        };

        view.Measure(new Size(1366, 768));
        view.Arrange(new Rect(0, 0, 1366, 768));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        view.DataContext = null;
    }

    private static void VerifyDailyCloseCashWorkspaceRuntime()
    {
        using var viewModel = new DailyCloseViewModel(
            new DailyCloseRuntimeService(),
            new DailyCloseRuntimePrintService(),
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0));
        var view = new DailyCloseView { DataContext = viewModel };
        var host = new Window
        {
            Width = 1080,
            Height = 720,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = view
        };

        host.Show();
        host.Activate();
        try
        {
            var tabControl = Assert.IsType<TabControl>(view.FindName("DailyCloseTabControl"));
            tabControl.ApplyTemplate();
            var toolbar = Assert.IsType<Border>(tabControl.Template.FindName("DailyCloseToolbar", tabControl));
            var toolbarLayout = Assert.IsType<Grid>(tabControl.Template.FindName("DailyCloseToolbarLayout", tabControl));
            var navigationTabs = Assert.IsType<Border>(tabControl.Template.FindName("DailyCloseNavigationTabsBorder", tabControl));
            Assert.Null(tabControl.Template.FindName("DailyCloseRefreshHistoryButton", tabControl));
            var createDraftButton = Assert.IsType<Button>(tabControl.Template.FindName("DailyCloseCreateDraftButton", tabControl));
            var continueDraftButton = Assert.IsType<Button>(tabControl.Template.FindName("DailyCloseContinueDraftButton", tabControl));
            var historyTab = Assert.IsType<TabItem>(tabControl.Items[0]);
            var linklyTab = Assert.IsType<TabItem>(tabControl.Items[1]);

            PumpDispatcher();

            // 刷新历史收进历史列表卡片，不再挤在顶栏里。
            var refreshHistoryButton = Assert.IsType<Button>(view.FindName("DailyCloseRefreshHistoryButton"));
            Assert.Empty(toolbarLayout.RowDefinitions);
            Assert.InRange(toolbar.ActualHeight, 60, 82);
            Assert.InRange(navigationTabs.ActualWidth, 307.5, double.MaxValue);
            Assert.InRange(historyTab.ActualWidth, 147.5, double.MaxValue);
            Assert.InRange(linklyTab.ActualWidth, 147.5, double.MaxValue);
            Assert.True(createDraftButton.IsVisible);
            Assert.False(continueDraftButton.IsVisible);
            AssertFullyContained(navigationTabs, toolbar);
            Assert.True(refreshHistoryButton.IsVisible);
            AssertFullyContained(refreshHistoryButton, view);
            AssertFullyContained(createDraftButton, toolbar);

            viewModel.HasDailyCloseDraft = true;
            PumpDispatcher();

            Assert.False(createDraftButton.IsVisible);
            Assert.True(continueDraftButton.IsVisible);
            AssertFullyContained(continueDraftButton, toolbar);

            viewModel.IsCashCountWorkspaceOpen = true;
            PumpDispatcher();

            var workspace = Assert.IsType<Grid>(view.FindName("DailyCloseCashWorkspaceOverlay"));
            var workspaceSurface = Assert.Single(workspace.Children.OfType<Border>());
            var workspaceBody = Assert.IsType<Grid>(view.FindName("DailyCloseCashWorkspaceBody"));
            var cashCountPanel = Assert.IsType<Border>(view.FindName("CashCountPanel"));
            var zReportPanel = Assert.IsType<Border>(view.FindName("DailyCloseZReportPanel"));
            var keypadPanel = Assert.IsType<Border>(view.FindName("CashCountKeypadPanel"));
            var discardButton = Assert.IsType<Button>(view.FindName("DailyCloseDiscardDraftButton"));
            var returnButton = Assert.IsType<Button>(view.FindName("DailyCloseCashWorkspaceReturnButton"));
            var saveButton = Assert.IsType<Button>(view.FindName("DailyCloseSaveAndFinalizeButton"));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(workspace));
            Assert.Same(returnButton, Keyboard.FocusedElement);
            Assert.True(returnButton.IsVisible);
            AssertFullyContained(workspaceSurface, workspace);
            AssertFullyContained(workspaceBody, workspaceSurface);
            AssertFullyContained(cashCountPanel, workspaceBody);
            AssertFullyContained(keypadPanel, workspaceBody);
            AssertFullyContained(zReportPanel, workspaceBody);
            AssertFullyContained(discardButton, workspaceSurface);
            AssertFullyContained(returnButton, workspaceSurface);
            AssertFullyContained(saveButton, workspaceSurface);

            // 数字键盘常驻工作区：打开即选中第一个面额，键盘与“应用 · 下一项”都在屏内可点。
            Assert.True(viewModel.IsCashCountDialogOpen);
            Assert.Same(viewModel.Denominations.First(), viewModel.SelectedCashDenomination);
            var applyButton = Assert.IsType<Button>(view.FindName("CashCountApplyButton"));
            var nineGrid = Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(view.FindName("CashCountNineGrid"));
            Assert.True(applyButton.IsVisible);
            AssertFullyContained(applyButton, keypadPanel);
            AssertFullyContained(nineGrid, keypadPanel);
            Assert.InRange(applyButton.ActualHeight, 44, double.MaxValue);
            Assert.All(nineGrid.Children.OfType<Button>(), key => Assert.InRange(key.ActualHeight, 44, double.MaxValue));

            // 键盘不再是二层弹窗：一次 Escape 即关闭工作区并保留草稿。
            RaiseEscapeFromFocusedElement();

            Assert.False(viewModel.IsCashCountWorkspaceOpen);
            Assert.True(viewModel.HasDailyCloseDraft);

            viewModel.IsCashCountWorkspaceOpen = true;
            PumpDispatcher();
            viewModel.RequestDiscardDailyCloseDraftCommand.Execute(null);
            PumpDispatcher();

            var discard = Assert.IsType<Grid>(view.FindName("DailyCloseDiscardDraftOverlay"));
            var discardCancel = Assert.IsType<Button>(view.FindName("DailyCloseDiscardDraftCancelButton"));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(discard));
            Assert.Same(discardCancel, Keyboard.FocusedElement);

            RaiseEscapeFromFocusedElement();

            Assert.False(viewModel.IsDiscardDailyCloseDraftConfirmationOpen);
            Assert.True(viewModel.IsCashCountWorkspaceOpen);
            Assert.True(viewModel.HasDailyCloseDraft);
            Assert.Same(returnButton, Keyboard.FocusedElement);

            host.Width = 2034;
            host.Height = 1140;
            PumpDispatcher();

            Assert.Equal(1600, workspaceSurface.MaxWidth);
            var availableWorkspaceWidth = Math.Max(
                0,
                workspace.ActualWidth - workspaceSurface.Margin.Left - workspaceSurface.Margin.Right);
            var expectedWorkspaceWidth = Math.Min(workspaceSurface.MaxWidth, availableWorkspaceWidth);
            Assert.InRange(
                workspaceSurface.ActualWidth,
                expectedWorkspaceWidth - 0.5,
                expectedWorkspaceWidth + 0.5);
            AssertFullyContained(workspaceSurface, workspace);
            AssertFullyContained(workspaceBody, workspaceSurface);
            AssertFullyContained(cashCountPanel, workspaceBody);
            AssertFullyContained(keypadPanel, workspaceBody);
            AssertFullyContained(zReportPanel, workspaceBody);
            AssertFullyContained(discardButton, workspaceSurface);
            AssertFullyContained(returnButton, workspaceSurface);
            AssertFullyContained(saveButton, workspaceSurface);
        }
        finally
        {
            host.Close();
            view.DataContext = null;
        }
    }

    private static void PumpDispatcher()
    {
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void RaiseEscapeFromFocusedElement()
    {
        var focusedElement = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
        var presentationSource = PresentationSource.FromVisual(focusedElement);
        Assert.NotNull(presentationSource);
        var keyEvent = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            presentationSource!,
            Environment.TickCount,
            Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };

        focusedElement.RaiseEvent(keyEvent);
        Assert.True(keyEvent.Handled);
        PumpDispatcher();
    }

    private static void AssertFullyContained(FrameworkElement child, FrameworkElement ancestor)
    {
        var origin = child.TransformToAncestor(ancestor).Transform(new Point());
        Assert.True(origin.X >= -0.5, $"{child.Name} 左侧超出弹窗边界。实际：{origin.X:0.##}");
        Assert.True(origin.Y >= -0.5, $"{child.Name} 顶部超出弹窗边界。实际：{origin.Y:0.##}");
        Assert.True(
            origin.X + child.ActualWidth <= ancestor.ActualWidth + 0.5,
            $"{child.Name} 右侧超出弹窗边界。");
        Assert.True(
            origin.Y + child.ActualHeight <= ancestor.ActualHeight + 0.5,
            $"{child.Name} 底部超出弹窗边界。");
    }

    private static void VerifyPosTerminalTouchLayout()
    {
        VerifyPosTerminalTouchLayoutAtSize(1080, 720);
        VerifyPosTerminalTouchLayoutAtSize(1366, 768);
    }

    private static void VerifyPosTerminalTouchLayoutAtSize(double targetWidth, double targetWindowHeight)
    {
        var targetContentHeight = targetWindowHeight - 54 - 42;
        var view = new PosTerminalView();
        var root = Assert.IsType<System.Windows.Controls.Grid>(view.Content);
        var cartItemsGrid = Assert.Single(root.Children
            .OfType<System.Windows.Controls.Grid>()
            .SelectMany(child => child.Children
                .OfType<Border>()
                .Select(border => border.Child)
                .OfType<DataGrid>()));
        cartItemsGrid.ItemsSource = Enumerable.Range(1, 4)
            .Select(index => new CartLayoutProbe(
                DisplayName: $"Product {index}",
                ItemNumber: $"SKU-{index:000}",
                LookupCode: $"69000{index}",
                ProductImage: null,
                UnitPrice: 4.5m,
                SignedQuantity: 1m,
                GrossAmount: 4.5m,
                ActualAmount: 4.5m,
                HasDiscount: false,
                DiscountRateText: string.Empty,
                HasZeroUnitPrice: false,
                IsReturnLine: false))
            .ToArray();

        // 直接按客户区尺寸布局，避免托管 runner 将离屏顶层窗口限制到桌面工作区。
        // Dispatcher 处理模板与容器生成后再布局一次，保证断言读取稳定的最终尺寸。
        var layoutSize = new Size(targetWidth, targetContentHeight);
        view.Measure(layoutSize);
        view.Arrange(new Rect(layoutSize));
        view.UpdateLayout();
        PumpDispatcher();
        view.Measure(layoutSize);
        view.Arrange(new Rect(layoutSize));
        view.UpdateLayout();

        Assert.Equal(3, root.ColumnDefinitions.Count);
        Assert.InRange(view.ActualWidth, targetWidth - 0.5, targetWidth + 0.5);
        Assert.InRange(root.ActualWidth, view.ActualWidth - 0.5, view.ActualWidth + 0.5);
        var cartWidth = root.ColumnDefinitions[0].ActualWidth;
        var keypadWidth = root.ColumnDefinitions[1].ActualWidth;
        var sidebarWidth = root.ColumnDefinitions[2].ActualWidth;
        Assert.True(cartWidth >= 600, $"{targetWidth:0}×{targetWindowHeight:0} 下购物车宽度不足：{cartWidth:0.##}。");
        Assert.True(keypadWidth >= 280, $"{targetWidth:0}×{targetWindowHeight:0} 下键盘宽度不足：{keypadWidth:0.##}。");
        Assert.True(sidebarWidth >= 180, $"{targetWidth:0}×{targetWindowHeight:0} 下右侧操作区宽度不足：{sidebarWidth:0.##}。");
        Assert.InRange(cartWidth + keypadWidth + sidebarWidth, targetWidth - 0.5, targetWidth + 0.5);
        if (targetWidth == 1366)
        {
            Assert.InRange(cartWidth, 790, 794);
            Assert.InRange(keypadWidth, 353, 357);
            Assert.InRange(sidebarWidth, 216, 220);
            Assert.True(cartWidth > sidebarWidth * 3.5, "1366×768 下购物车应明显宽于右侧操作区。");
        }

        var cartSummary = Assert.IsType<Border>(view.FindName("CartSummaryPanel"));
        var totalsSummary = Assert.IsType<Border>(view.FindName("CartTotalsSummaryRow"));
        var inputBuffer = Assert.IsType<Border>(view.FindName("InputBufferHost"));
        Assert.InRange(cartSummary.ActualHeight, 91.5, 92.5);
        Assert.InRange(totalsSummary.ActualHeight, 57.5, 58.5);
        Assert.InRange(inputBuffer.ActualHeight, 77.5, 78.5);

        Assert.Equal(5, cartItemsGrid.Columns.Count);
        Assert.InRange(cartItemsGrid.Columns[0].ActualWidth, 43.5, 44.5);
        Assert.True(
            cartItemsGrid.Columns.Sum(column => column.ActualWidth) <= cartItemsGrid.ActualWidth + 0.5,
            $"{targetWidth:0}×{targetWindowHeight:0} 下购物车列宽超出可视区域。");

        var cartRows = Enumerable.Range(0, 4)
            .Select(index => Assert.IsType<DataGridRow>(cartItemsGrid.ItemContainerGenerator.ContainerFromIndex(index)))
            .ToArray();
        Assert.All(cartRows, row => Assert.True(row.ActualHeight >= 67));
        for (var index = 0; index < cartRows.Length; index++)
        {
            var rowNumberCell = FindVisualDescendants<DataGridCell>(cartRows[index])
                .Single(cell => cell.Column.DisplayIndex == 0);
            var rowNumber = Assert.Single(FindVisualDescendants<TextBlock>(rowNumberCell));
            Assert.Equal((index + 1).ToString(), rowNumber.Text);
        }

        var firstRowCells = FindVisualDescendants<DataGridCell>(cartRows[0])
            .OrderBy(cell => cell.Column.DisplayIndex)
            .ToArray();
        Assert.Equal(5, firstRowCells.Length);
        Assert.All(firstRowCells, cell => AssertHorizontallyContained(cell, cartRows[0], targetWidth, targetWindowHeight));

        var metadataLine = Assert.Single(FindVisualDescendants<StackPanel>(firstRowCells[1])
            .Where(panel => panel.Name == "CartItemMetadataLine"));
        AssertHorizontallyContained(metadataLine, firstRowCells[1], targetWidth, targetWindowHeight);

        var quantityButtons = FindVisualDescendants<Button>(firstRowCells[3]).ToArray();
        Assert.Equal(2, quantityButtons.Length);
        Assert.All(quantityButtons, button =>
        {
            Assert.Equal(32, button.ActualWidth);
            AssertHorizontallyContained(button, firstRowCells[3], targetWidth, targetWindowHeight);
        });

        var totalTexts = FindVisualDescendants<TextBlock>(firstRowCells[4]).ToArray();
        Assert.NotEmpty(totalTexts);
        Assert.All(totalTexts, text => AssertHorizontallyContained(text, firstRowCells[4], targetWidth, targetWindowHeight));

        var deleteButton = Assert.Single(FindVisualDescendants<Button>(cartRows[0]).Where(button =>
            button.Name == "PART_SwipeDeleteAction"));
        Assert.Equal(88, deleteButton.ActualWidth);
        Assert.True(deleteButton.ActualHeight >= 67, $"删除操作触控高度不足：{deleteButton.ActualHeight:0.##}。");
        AssertHorizontallyContained(deleteButton, cartRows[0], targetWidth, targetWindowHeight);

        var keypad = Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(view.FindName("CashierKeypad"));
        var numericParameters = new HashSet<string>(["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "."]);
        var numericButtons = keypad.Children
            .OfType<Button>()
            .Where(button => numericParameters.Contains(button.CommandParameter?.ToString() ?? string.Empty))
            .ToArray();
        Assert.Equal(11, numericButtons.Length);
        Assert.All(numericButtons, button =>
        {
            Assert.Equal(28, button.FontSize);
            Assert.Equal(FontWeights.Black, button.FontWeight);
            Assert.True(button.ActualHeight >= 44, $"数字键触控高度不足：{button.ActualHeight:0.##}。");
        });

        var sidebar = Assert.IsType<System.Windows.Controls.Grid>(view.FindName("AttendanceQrSidebar"));
        var actionGrid = Assert.Single(sidebar.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>());
        var actionButtons = actionGrid.Children.OfType<System.Windows.Controls.Button>().ToArray();
        Assert.Equal(10, actionButtons.Length);
        Assert.All(actionButtons, button =>
        {
            var minimumWidth = targetWidth == 1366 ? 90 : 76;
            Assert.True(button.ActualWidth >= minimumWidth, $"右侧按钮触控宽度不足：{button.ActualWidth:0.##}。");
            Assert.True(button.ActualHeight >= 62, $"右侧按钮触控高度不足：{button.ActualHeight:0.##}。");
        });

    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void AssertHorizontallyContained(
        FrameworkElement child,
        FrameworkElement ancestor,
        double targetWidth,
        double targetWindowHeight)
    {
        var origin = child.TransformToAncestor(ancestor).Transform(new Point());
        Assert.True(
            origin.X >= -0.5 && origin.X + child.ActualWidth <= ancestor.ActualWidth + 0.5,
            $"{targetWidth:0}×{targetWindowHeight:0} 下 {child.GetType().Name} 超出 {ancestor.GetType().Name} 水平边界：" +
            $"起点 {origin.X:0.##}，宽度 {child.ActualWidth:0.##}，容器宽度 {ancestor.ActualWidth:0.##}。");
    }

    private static void VerifyUnloadedViewInstancesAreCollectible()
    {
        using var settingsViewModel = CreateSettingsViewModel();
        using var historyViewModel = new TransactionHistoryViewModel();
        var paymentViewModel = new CountingPropertyChangedSource();

        var settingsView = CreateReleasedSettingsView(settingsViewModel);
        var historyView = CreateReleasedHistoryView(historyViewModel);
        var paymentView = CreateReleasedPaymentView(paymentViewModel);

        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        ForceFullCollection();

        Assert.False(settingsView.IsAlive, "SettingsView 在 Loaded/Unloaded 循环后仍被 VM 强事件持有。");
        Assert.False(historyView.IsAlive, "TransactionHistoryView 在 Loaded/Unloaded 循环后仍被 VM 强事件持有。");
        Assert.False(paymentView.IsAlive, "PaymentView 在 Loaded/Unloaded 循环后仍被 VM 强事件持有。");

        // 保证三个 VM 在弱引用断言期间仍是 GC 根，避免把 VM 与 View 一起回收造成假阳性。
        GC.KeepAlive(settingsViewModel);
        GC.KeepAlive(historyViewModel);
        GC.KeepAlive(paymentViewModel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedSettingsView(SettingsViewModel viewModel)
    {
        var view = new SettingsView { DataContext = viewModel };
        ExerciseLoadedCycles(view);
        AssertDirectHandlerCount(viewModel, view, "ViewModel_PropertyChanged", 0);
        Assert.Null(ReadPrivateField(view, "_viewModel"));
        return new WeakReference(view);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedHistoryView(TransactionHistoryViewModel viewModel)
    {
        var view = new TransactionHistoryView { DataContext = viewModel };
        ExerciseLoadedCycles(view);
        AssertDirectHandlerCount(viewModel, view, "ViewModelPropertyChanged", 0);
        Assert.Null(ReadPrivateField(view, "_viewModel"));
        return new WeakReference(view);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedPaymentView(CountingPropertyChangedSource viewModel)
    {
        var view = new PaymentView { DataContext = viewModel };
        ExerciseLoadedCycles(view);
        AssertDirectHandlerCount(viewModel, view, "PaymentViewModelPropertyChanged", 0);
        Assert.Null(ReadPrivateField(view, "_viewModelNotifications"));
        return new WeakReference(view);
    }

    private static void ExerciseLoadedCycles(FrameworkElement view)
    {
        for (var cycle = 0; cycle < 2; cycle++)
        {
            RaiseLoaded(view);
            RaiseUnloaded(view);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndReplacePaymentViewModel(
        PaymentView view,
        CountingPropertyChangedSource replacement)
    {
        var previous = new CountingPropertyChangedSource();
        view.DataContext = previous;
        AssertDirectHandlerCount(previous, view, "PaymentViewModelPropertyChanged", 0);
        RaiseLoaded(view);
        AssertDirectHandlerCount(previous, view, "PaymentViewModelPropertyChanged", 1);
        RaiseLoaded(view);
        AssertDirectHandlerCount(previous, view, "PaymentViewModelPropertyChanged", 1);

        // Payment 是缓存页面；仅隐藏而未 Unloaded 时必须继续接收 VM 通知。
        view.Visibility = Visibility.Hidden;
        AssertDirectHandlerCount(previous, view, "PaymentViewModelPropertyChanged", 1);
        view.Visibility = Visibility.Visible;

        var weakReference = new WeakReference(previous);
        view.DataContext = replacement;
        AssertDirectHandlerCount(previous, view, "PaymentViewModelPropertyChanged", 0);
        return weakReference;
    }

    private static SettingsViewModel CreateSettingsViewModel()
    {
        var setupService = DispatchProxy.Create<ICardTerminalSetupService, ThrowingDispatchProxy>();
        return new SettingsViewModel(setupService);
    }

    private static void RaiseLoaded(FrameworkElement view) =>
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));

    private static void RaiseUnloaded(FrameworkElement view) =>
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, view));

    private static object? ReadPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance);
    }

    private static void InvokePrivateMethod(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(instance, null);
    }

    private static void AssertDirectHandlerCount(
        INotifyPropertyChanged source,
        object target,
        string methodName,
        int expected)
    {
        var handlers = ReadPropertyChangedHandlers(source);
        var count = handlers?
            .GetInvocationList()
            .Count(handler =>
                ReferenceEquals(handler.Target, target) &&
                string.Equals(handler.Method.Name, methodName, StringComparison.Ordinal)) ?? 0;
        Assert.Equal(expected, count);
    }

    private static Delegate? ReadPropertyChangedHandlers(INotifyPropertyChanged source)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
        {
            var field = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate =>
                    candidate.FieldType == typeof(PropertyChangedEventHandler) &&
                    candidate.Name.Contains("PropertyChanged", StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field.GetValue(source) as Delegate;
            }
        }

        throw new InvalidOperationException($"找不到 {source.GetType().FullName} 的 PropertyChanged 事件字段。");
    }

    private static void ForceFullCollection()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class CountingPropertyChangedSource : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _propertyChanged;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _propertyChanged += value;
            remove => _propertyChanged -= value;
        }
    }

    private sealed record CartLayoutProbe(
        string DisplayName,
        string ItemNumber,
        string LookupCode,
        string? ProductImage,
        decimal UnitPrice,
        decimal SignedQuantity,
        decimal GrossAmount,
        decimal ActualAmount,
        bool HasDiscount,
        string DiscountRateText,
        bool HasZeroUnitPrice,
        bool IsReturnLine);

    private sealed class CashCountDialogBindingSource
    {
        public bool IsCashCountWorkspaceOpen => true;

        public bool IsCashCountDialogOpen => true;

        public bool IsDiscardDailyCloseDraftConfirmationOpen => false;

        public string BusinessDateText => "Tue, 25 Aug 2026";

        public int CashCountDialogQuantity => 2;

        public decimal CashCountDialogSubtotal => 200m;

        public string KeypadBuffer => "2";

        public CashCountDialogDenomination SelectedCashDenomination { get; } = new();
    }

    private sealed class CashCountDialogDenomination
    {
        public string Label => "$100";
    }

    private sealed class DailyCloseRuntimeService : IDailyCloseService
    {
        public IReadOnlyList<CashDenomination> Denominations => DailyCloseService.AustralianDenominations;

        public Task<DailyCloseReport> LoadReportAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("运行时弹窗测试不应加载日结汇总。");
        }

        public Task<DailyCloseArchive> SaveAsync(
            PosSessionState session,
            DateTime businessDate,
            IReadOnlyList<CashDenominationCount> cashCounts,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("运行时弹窗测试不应保存日结。");
        }

        public Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("运行时弹窗测试不应加载日结历史。");
        }
    }

    private sealed class DailyCloseRuntimePrintService : IDailyClosePrintService
    {
        public Task<ReceiptPrintDocument> BuildDocumentAsync(
            DailyCloseArchive archive,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("运行时弹窗测试不应生成打印文档。");
        }

        public Task<ReceiptPrintResult> PrintAsync(
            DailyCloseArchive archive,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("运行时弹窗测试不应调用打印机。");
        }
    }

    public class ThrowingDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"测试不应调用 {targetMethod?.Name}。");
    }
}
