using System.Globalization;
using System.Net;
using BlazorApp.Shared.DTOs;
using Hbpos.Contracts.HeldOrders;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using InstallmentPaymentDto = Hbpos.Contracts.Installments.InstallmentPaymentDto;
using LocalClaimStatus = Hbpos.Client.Wpf.Models.SharedHeldOrderClaimStatus;
using HeldServerStatus = Hbpos.Contracts.HeldOrders.SharedHeldOrderStatus;
using static Hbpos.Client.Tests.SharedHeldOrderClientTestSupport;

namespace Hbpos.Client.Tests;

public sealed class TransactionHistoryViewModelTests
{
    private static readonly DateTimeOffset HeldFixtureTime = new(DateTime.Today.AddHours(9));
    private static readonly DateTime HeldFixtureDate = HeldFixtureTime.Date;
    private static readonly string HeldFixtureTimeIso = HeldFixtureTime.ToUniversalTime().ToString("O");

    [Theory]
    [InlineData(TransactionHistorySource.LocalOrders)]
    [InlineData(TransactionHistorySource.RemoteOrders)]
    public void Return_order_highlight_follows_negative_actual_amount(TransactionHistorySource source)
    {
        var item = new HistoryOrderListItem(
            Guid.NewGuid(), source, "S001", "POS-01", "Alice",
            DateTimeOffset.UtcNow, -1m, 0m, -1m, 0, "Card", "Completed");

        Assert.True(item.IsReturnOrder);
        Assert.False((item with { ActualAmount = 0m }).IsReturnOrder);
        Assert.False((item with { ActualAmount = 1m }).IsReturnOrder);
    }

    [Theory]
    [InlineData("Synced", true)]
    [InlineData("Pending", true)]
    [InlineData("Failed", true)]
    [InlineData("Syncing", false)]
    public void Local_order_reupload_eligibility_matches_sync_status(string status, bool expected)
    {
        var item = new HistoryOrderListItem(
            Guid.NewGuid(), TransactionHistorySource.LocalOrders, "S001", "POS-01", "Alice",
            DateTimeOffset.UtcNow, 1m, 0m, 1m, 1, "Cash", status, SyncStatus: status);

        Assert.Equal(expected, item.CanReupload);
        Assert.False((item with { Source = TransactionHistorySource.RemoteOrders }).CanReupload);
        Assert.False((item with { IsSuspendedOrder = true }).CanReupload);
        Assert.False((item with { IsInstallmentOrder = true }).CanReupload);
    }

    [Fact]
    public async Task Scanner_barcode_is_trimmed_and_loads_current_source_and_filters()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var remoteOrders = new CapturingRemoteOrderHistoryService();
        using var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession(deviceCode: "POS-04"))
        {
            DateFrom = new DateTime(2026, 8, 20),
            DateTo = new DateTime(2026, 8, 22)
        };
        viewModel.IsOnlineSourceSelected = true;

        Assert.True(viewModel.ProcessScannerBarcode("  ITEM-42  ", "scanner-device", "keyboard-fallback"));
        var loadTask = viewModel.LoadCommand.ExecutionTask;
        Assert.NotNull(loadTask);
        await loadTask!;

        Assert.Equal(TransactionHistorySource.RemoteOrders, viewModel.SelectedSource);
        Assert.Equal("ITEM-42", viewModel.SearchText);
        Assert.Null(receiptQuery.LastQuery);
        Assert.Equal("ITEM-42", remoteOrders.LastQuery?.Keyword);
        Assert.Equal("POS-04", remoteOrders.LastQuery?.DeviceCode);
        Assert.Equal(new DateTime(2026, 8, 20), remoteOrders.LastQuery?.SoldFrom?.Date);
        Assert.Equal(new DateTime(2026, 8, 22), remoteOrders.LastQuery?.SoldTo?.Date);
    }

    [Fact]
    public async Task Scanner_consumes_barcode_while_direct_query_is_running_without_replacing_current_query()
    {
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryGate = new TaskCompletionSource<IReadOnlyList<LocalOrderSummary>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receiptQuery = new CapturingReceiptQueryService
        {
            QueryHandler = (_, _) =>
            {
                queryStarted.TrySetResult();
                return queryGate.Task;
            }
        };
        using var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            null,
            CreateSession());
        viewModel.SearchText = "FIRST-001";

        var loadTask = viewModel.LoadAsync();
        await queryStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var secondScanConsumed = viewModel.ProcessScannerBarcode("SECOND-002", "scanner-device", "raw");
        queryGate.SetResult([]);
        await loadTask;

        Assert.True(secondScanConsumed);
        Assert.Equal("FIRST-001", viewModel.SearchText);
        Assert.Equal("FIRST-001", receiptQuery.LastQuery?.Keyword);
        Assert.Equal(1, receiptQuery.QueryCallCount);
    }

    [Fact]
    public void Scanner_consumes_emergency_tokens_and_modal_scans_without_querying_or_replacing_search()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        using var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            null,
            CreateSession())
        {
            SearchText = "CURRENT-RESULT"
        };

        Assert.True(viewModel.ProcessScannerBarcode("   ", "scanner-device", "raw"));
        Assert.True(viewModel.ProcessScannerBarcode(" HBPOSE1-K1-AA-BB ", "scanner-device", "raw"));

        viewModel.IsReceiptPreviewOpen = true;
        Assert.True(viewModel.ProcessScannerBarcode("RECEIPT-MODAL", "scanner-device", "raw"));
        viewModel.IsReceiptPreviewOpen = false;

        viewModel.IsOrderDetailsOpen = true;
        Assert.True(viewModel.ProcessScannerBarcode("DETAIL-MODAL", "scanner-device", "raw"));
        viewModel.IsOrderDetailsOpen = false;

        viewModel.IsForceReleaseReasonPromptOpen = true;
        Assert.True(viewModel.ProcessScannerBarcode("FORCE-RELEASE-MODAL", "scanner-device", "raw"));

        Assert.Equal("CURRENT-RESULT", viewModel.SearchText);
        Assert.Equal(0, receiptQuery.QueryCallCount);
        Assert.Null(receiptQuery.LastQuery);
    }

    [Fact]
    public async Task Scanner_consumes_raw_input_while_confirmation_dialog_is_open()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var confirmationResult = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationDialog = new CapturingConfirmationDialogService
        {
            OrderDateRangeResultTask = confirmationResult.Task
        };
        var uploadService = new CallbackOrderExecutor(
            ids => new OrderUploadExecutionResult(ids.Count, ids.Count, 0),
            [Guid.NewGuid()]);
        using var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            null,
            CreateSession(),
            orderUploadExecutionService: uploadService,
            confirmationDialogService: confirmationDialog)
        {
            SearchText = "CURRENT-RESULT"
        };

        var reuploadTask = viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => confirmationDialog.CallCount == 1);

        Assert.True(viewModel.ProcessScannerBarcode("BACKGROUND-QUERY", "scanner-device", "raw"));
        Assert.Equal("CURRENT-RESULT", viewModel.SearchText);
        Assert.Equal(0, receiptQuery.QueryCallCount);

        confirmationResult.SetResult(false);
        await reuploadTask;
    }

    [Fact]
    public async Task Raw_scanner_subscription_routes_barcode_and_dispose_unsubscribes_once()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var scanner = new TrackingRawScannerService();
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            null,
            CreateSession(),
            rawScannerService: scanner);

        Assert.Equal(TransactionHistoryViewModel.PageId, viewModel.ScannerPageId);
        Assert.Equal(1, scanner.SubscribeCount);
        Assert.True(scanner.HasSubscription(TransactionHistoryViewModel.PageId));

        scanner.Emit(TransactionHistoryViewModel.PageId, "  RAW-9001  ");
        var loadTask = viewModel.LoadCommand.ExecutionTask;
        Assert.NotNull(loadTask);
        await loadTask!;

        Assert.Equal("RAW-9001", viewModel.SearchText);
        Assert.Equal("RAW-9001", receiptQuery.LastQuery?.Keyword);

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.Equal(1, scanner.UnsubscribeCount);
        Assert.False(scanner.HasSubscription(TransactionHistoryViewModel.PageId));
    }

    [Fact]
    public async Task Focused_search_enter_does_not_repeat_raw_scanner_query()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var scanner = new TrackingRawScannerService();
        using var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            null,
            CreateSession(),
            rawScannerService: scanner)
        {
            SearchText = "PREVIOUS-QUERY"
        };

        scanner.Emit(TransactionHistoryViewModel.PageId, "RAW-FOCUSED-9001");
        var rawLoadTask = viewModel.LoadCommand.ExecutionTask;
        Assert.NotNull(rawLoadTask);
        await rawLoadTask!;

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("RAW-FOCUSED-9001", viewModel.SearchText);
        Assert.Equal(1, receiptQuery.QueryCallCount);
        Assert.Equal("RAW-FOCUSED-9001", receiptQuery.LastQuery?.Keyword);
    }

    [Fact]
    public async Task Manual_enter_and_repeated_load_command_still_query_with_scanner_deduplicator()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var scanner = new TrackingRawScannerService();
        using var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            null,
            CreateSession(),
            rawScannerService: scanner)
        {
            SearchText = "MANUAL-ENTER-1001"
        };

        await viewModel.LoadCommand.ExecuteAsync(null);
        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, receiptQuery.QueryCallCount);
        Assert.Equal("MANUAL-ENTER-1001", receiptQuery.LastQuery?.Keyword);
    }

    [Fact]
    public async Task Order_details_reuses_loaded_receipt_and_resolves_product_images_by_priority()
    {
        var orderGuid = Guid.NewGuid();
        var soldAt = new DateTimeOffset(2026, 8, 26, 9, 30, 0, TimeSpan.Zero);
        var receipt = new ReceiptDetails(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            soldAt,
            50m,
            2m,
            48m,
            [
                new ReceiptPreviewLine("Snapshot item", "BAR-1", 1m, 10m, 0m, 10m)
                {
                    ProductCode = "P-1",
                    ItemNumber = "ITEM-1",
                    ProductImage = "snapshot://image-1"
                },
                new ReceiptPreviewLine("Exact lookup item", "BAR-2", 1m, 10m, 0m, 10m)
                {
                    ProductCode = "P-2",
                    ItemNumber = "ITEM-2"
                },
                new ReceiptPreviewLine("Barcode item", "BAR-3", 1m, 10m, 0m, 10m)
                {
                    ProductCode = "P-3",
                    ItemNumber = "ITEM-3"
                },
                new ReceiptPreviewLine("Metadata item", "HISTORY-4", 1m, 10m, 0m, 10m)
                {
                    ProductCode = "P-4",
                    ItemNumber = "ITEM-4"
                },
                new ReceiptPreviewLine("Identity mismatch", "BAR-5", 1m, 10m, 2m, 8m)
                {
                    ProductCode = "P-HISTORY-5",
                    ItemNumber = "ITEM-HISTORY-5"
                }
            ],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 50m, null)],
            TenderedAmount: 55m,
            ChangeAmount: 5m,
            OrderDisplay: "ORDER-DETAIL-001");
        var receiptQuery = new CapturingReceiptQueryService
        {
            Orders =
            [
                new LocalOrderSummary(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    soldAt,
                    50m,
                    2m,
                    48m,
                    "Synced",
                    5,
                    "Cash")
            ],
            Receipts = { [orderGuid] = receipt }
        };
        var catalog = new LocalSellableItemIndex();
        catalog.ReplaceAll(
        [
            CatalogItem("P-1", "BAR-1", "ITEM-1", "BAR-1", "catalog://image-1"),
            CatalogItem("P-2", "BAR-2", "ITEM-2", "BAR-2", "catalog://image-2"),
            CatalogItem("P-3", "ALT-3", "ITEM-3", "BAR-3", "catalog://image-3"),
            CatalogItem("P-4", "ALT-4", "ITEM-4", "BAR-4", "catalog://image-4"),
            CatalogItem("P-CATALOG-5", "BAR-5", "ITEM-CATALOG-5", "BAR-5", "catalog://wrong-image")
        ]);
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            null,
            null,
            CreateSession(),
            localSellableItemIndex: catalog);

        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Orders);
        Assert.Collection(
            viewModel.OrderDetailLinesForTests,
            line => Assert.Equal("snapshot://image-1", line.ProductImage),
            line => Assert.Equal("catalog://image-2", line.ProductImage),
            line => Assert.Equal("catalog://image-3", line.ProductImage),
            line => Assert.Equal("catalog://image-4", line.ProductImage),
            line => Assert.Null(line.ProductImage));
        var firstLine = viewModel.OrderDetailLinesForTests[0];
        Assert.Equal("ITEM-1", firstLine.ItemNumberDisplay);
        Assert.Equal("BAR-1", firstLine.LookupCodeDisplay);
        Assert.True(viewModel.HasOrderDetailsPayments);
        Assert.True(viewModel.HasOrderDetailsTenderedAmount);
        Assert.True(viewModel.HasOrderDetailsChangeAmount);
        Assert.True(viewModel.IsOrderDetailsFinancialContentVisible);
        Assert.Equal("ORDER-DETAIL-001", viewModel.OrderDetailsOrderId);
        Assert.Equal(1, receiptQuery.GetReceiptCallCount);

        viewModel.OpenReceiptPreviewCommand.Execute(row);
        Assert.True(viewModel.IsReceiptPreviewOpen);
        viewModel.OpenOrderDetailsCommand.Execute(row);

        Assert.True(viewModel.IsOrderDetailsOpen);
        Assert.False(viewModel.IsReceiptPreviewOpen);
        Assert.Equal(1, receiptQuery.GetReceiptCallCount);

        viewModel.OpenReceiptPreviewCommand.Execute(row);
        Assert.True(viewModel.IsReceiptPreviewOpen);
        Assert.False(viewModel.IsOrderDetailsOpen);
    }

    [Fact]
    public async Task Remote_only_held_order_cannot_open_details_or_read_receipt_when_selected()
    {
        var remoteOnlyHeldOrder = new HistoryOrderListItem(
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
            IsSuspendedOrder: false,
            CanRecall: true,
            IsHeldOrder: true,
            CanRemoteRecall: true);
        var receiptQuery = new CapturingReceiptQueryService();
        var viewModel = new TransactionHistoryViewModel(receiptQuery);

        Assert.False(remoteOnlyHeldOrder.CanViewOrderDetails);
        Assert.False(viewModel.OpenOrderDetailsCommand.CanExecute(remoteOnlyHeldOrder));

        viewModel.OpenOrderDetailsCommand.Execute(remoteOnlyHeldOrder);

        Assert.False(viewModel.IsOrderDetailsOpen);
        Assert.Null(viewModel.SelectedOrder);
        Assert.Null(viewModel.SelectedReceipt);

        viewModel.SelectedOrder = remoteOnlyHeldOrder;
        await WaitUntilAsync(() => !viewModel.IsReceiptPreviewLoading);

        Assert.Same(remoteOnlyHeldOrder, viewModel.SelectedOrder);
        Assert.Null(viewModel.SelectedReceipt);
        Assert.Equal(0, receiptQuery.GetReceiptCallCount);
    }

    [Fact]
    public async Task Held_order_detail_failure_keeps_loaded_rows_and_exposes_retryable_error()
    {
        var orderGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.Now,
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending)
            ],
            GetOrderException = new InvalidOperationException("simulated held detail failure")
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            null,
            CreateSession());

        viewModel.IsHeldSourceSelected = true;
        await WaitUntilAsync(() =>
            !viewModel.IsReceiptPreviewLoading &&
            viewModel.OrderDetailsErrorMessage == "simulated held detail failure");

        var row = Assert.Single(viewModel.Orders);
        Assert.Equal(orderGuid, row.OrderGuid);
        Assert.Null(viewModel.SelectedReceipt);
        Assert.Equal("simulated held detail failure", viewModel.StatusMessage);

        viewModel.OpenOrderDetailsCommand.Execute(row);
        await WaitUntilAsync(() =>
            !viewModel.IsReceiptPreviewLoading &&
            viewModel.OrderDetailsErrorMessage == "simulated held detail failure");

        Assert.True(viewModel.IsOrderDetailsOpen);
        Assert.True(viewModel.RetryOrderDetailsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Held_history_ignores_older_detail_failure_after_new_selection_succeeds()
    {
        var firstOrderGuid = Guid.NewGuid();
        var secondOrderGuid = Guid.NewGuid();
        var now = DateTimeOffset.Now;
        var firstRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestGate = new TaskCompletionSource<SuspendedOrder?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOrder = new SuspendedOrder(
            secondOrderGuid,
            "S001",
            "POS-01",
            "C001",
            "Bob",
            now.AddMinutes(-1),
            20m,
            0m,
            20m,
            SuspendedOrderStatus.Pending,
            []);
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    firstOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    now,
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending),
                new SuspendedOrderSummary(
                    secondOrderGuid,
                    "S001",
                    "POS-01",
                    "Bob",
                    now.AddMinutes(-1),
                    20m,
                    0m,
                    20m,
                    0,
                    SuspendedOrderStatus.Pending)
            ],
            GetOrderHandler = (orderGuid, _) =>
            {
                if (orderGuid == firstOrderGuid)
                {
                    firstRequestStarted.TrySetResult();
                    return firstRequestGate.Task;
                }

                return Task.FromResult<SuspendedOrder?>(
                    orderGuid == secondOrderGuid ? secondOrder : null);
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            null,
            CreateSession());
        var suppressAutoLoadField = typeof(TransactionHistoryViewModel).GetField(
            "_suppressSourceAutoLoad",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(suppressAutoLoadField);
        suppressAutoLoadField!.SetValue(viewModel, true);
        try
        {
            viewModel.IsHeldSourceSelected = true;
        }
        finally
        {
            suppressAutoLoadField.SetValue(viewModel, false);
        }

        var initialLoad = viewModel.LoadAsync();
        await firstRequestStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var secondRow = Assert.Single(viewModel.Orders, order => order.OrderGuid == secondOrderGuid);
        viewModel.SelectedOrder = secondRow;
        await WaitUntilAsync(() =>
            viewModel.SelectedReceipt?.OrderGuid == secondOrderGuid &&
            !viewModel.IsReceiptPreviewLoading);

        firstRequestGate.SetException(new InvalidOperationException("stale held detail failure"));
        await initialLoad;

        Assert.Equal(secondOrderGuid, viewModel.SelectedOrder?.OrderGuid);
        Assert.Equal(secondOrderGuid, viewModel.SelectedReceipt?.OrderGuid);
        Assert.Equal(string.Empty, viewModel.OrderDetailsErrorMessage);
        Assert.Equal(string.Empty, viewModel.StatusMessage);
        Assert.False(viewModel.IsReceiptPreviewLoading);
        Assert.Equal(2, viewModel.Orders.Count);
    }

    [Fact]
    public void Order_details_exposes_empty_state_for_loaded_order_without_lines()
    {
        var orderGuid = Guid.NewGuid();
        var order = new HistoryOrderListItem(
            orderGuid,
            TransactionHistorySource.LocalOrders,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            0m,
            0m,
            0m,
            0,
            string.Empty,
            "Synced");
        var viewModel = new TransactionHistoryViewModel();
        viewModel.Orders.Add(order);
        viewModel.SelectedOrder = order;
        viewModel.SelectedReceipt = new ReceiptDetails(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            0m,
            0m,
            0m,
            [],
            []);

        viewModel.OpenOrderDetailsCommand.Execute(order);

        Assert.True(viewModel.IsOrderDetailsOpen);
        Assert.True(viewModel.IsOrderDetailsEmpty);
        Assert.Empty(viewModel.OrderDetailLinesForTests);
        Assert.False(viewModel.HasOrderDetailsPayments);
        Assert.True(viewModel.IsOrderDetailsFinancialContentVisible);
        Assert.Equal(string.Empty, viewModel.OrderDetailsErrorMessage);
    }

    [Fact]
    public async Task Order_details_failure_can_retry_without_reopening_or_using_stale_data()
    {
        var orderGuid = Guid.NewGuid();
        var receipt = new ReceiptDetails(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            6m,
            0m,
            6m,
            [new ReceiptPreviewLine("Retry item", "BAR-RETRY", 1m, 6m, 0m, 6m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 6m, null)]);
        var receiptQuery = new CapturingReceiptQueryService
        {
            Orders =
            [
                new LocalOrderSummary(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.UtcNow,
                    6m,
                    0m,
                    6m,
                    "Synced",
                    1,
                    "Cash")
            ]
        };
        receiptQuery.ReceiptHandler = (_, _) => receiptQuery.GetReceiptCallCount switch
        {
            1 => Task.FromResult<ReceiptDetails?>(null),
            2 => Task.FromException<ReceiptDetails?>(new InvalidOperationException("simulated detail failure")),
            _ => Task.FromResult<ReceiptDetails?>(receipt)
        };
        var viewModel = new TransactionHistoryViewModel(receiptQuery, null, null, CreateSession());

        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);
        viewModel.OpenOrderDetailsCommand.Execute(row);
        await WaitUntilAsync(() =>
            !viewModel.IsReceiptPreviewLoading &&
            viewModel.OrderDetailsErrorMessage == "simulated detail failure");

        Assert.True(viewModel.IsOrderDetailsOpen);
        Assert.Empty(viewModel.OrderDetailLinesForTests);
        Assert.False(viewModel.IsOrderDetailsFinancialContentVisible);
        Assert.True(viewModel.RetryOrderDetailsCommand.CanExecute(null));

        viewModel.RetryOrderDetailsCommand.Execute(null);
        await WaitUntilAsync(() => ReferenceEquals(viewModel.SelectedReceipt, receipt));

        Assert.True(viewModel.IsOrderDetailsOpen);
        Assert.Equal(string.Empty, viewModel.OrderDetailsErrorMessage);
        Assert.Equal("Retry item", Assert.Single(viewModel.OrderDetailLinesForTests).DisplayName);
        Assert.Equal(3, receiptQuery.GetReceiptCallCount);
    }

    [Fact]
    public async Task Reupload_selected_executes_ids_and_refreshes_history_status()
    {
        var orderGuid = Guid.NewGuid();
        var failed = new LocalOrderSummary(
            orderGuid, "S001", "POS-01", "Alice", DateTimeOffset.UtcNow,
            10m, 0m, 10m, "Failed", 1, "Cash");
        var receiptQuery = new CapturingReceiptQueryService { Orders = [failed] };
        var executor = new CallbackOrderExecutor(ids =>
        {
            receiptQuery.Orders = [failed with { SyncStatus = "Synced" }];
            return new OrderUploadExecutionResult(ids.Count, ids.Count, 0);
        });
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor);
        await viewModel.LoadAsync();
        viewModel.Orders[0].Selection.IsSelected = true;

        await viewModel.ReuploadSelectedCommand.ExecuteAsync(null);

        Assert.Equal([orderGuid], executor.SelectedIds);
        var refreshed = Assert.Single(viewModel.Orders);
        Assert.Equal("Synced", refreshed.SyncStatus);
        Assert.False(refreshed.Selection.IsSelected);
    }

    [Fact]
    public async Task Reupload_selected_with_empty_selection_does_nothing()
    {
        var failed = new LocalOrderSummary(
            Guid.NewGuid(), "S001", "POS-01", "Alice", DateTimeOffset.UtcNow,
            10m, 0m, 10m, "Failed", 1, "Cash");
        var executor = new CallbackOrderExecutor(
            ids => new OrderUploadExecutionResult(ids.Count, ids.Count, 0));
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService { Orders = [failed] },
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor);
        await viewModel.LoadAsync();

        await viewModel.ReuploadSelectedCommand.ExecuteAsync(null);

        Assert.Equal(0, executor.SelectedCallCount);
        Assert.Equal(string.Empty, viewModel.StatusMessage);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(499, 1)]
    [InlineData(500, 1)]
    [InlineData(501, 2)]
    public async Task Reupload_date_range_splits_candidates_into_500_order_batches(int orderCount, int expectedBatchCount)
    {
        var orderGuids = Enumerable.Range(0, orderCount).Select(_ => Guid.NewGuid()).ToArray();
        var executor = new CallbackOrderExecutor(
            ids => new OrderUploadExecutionResult(ids.Count, ids.Count, 0),
            orderGuids);
        var confirmation = new CapturingConfirmationDialogService { Result = true };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor,
            confirmationDialogService: confirmation)
        {
            DateFrom = new DateTime(2026, 7, 1),
            DateTo = new DateTime(2026, 7, 1)
        };

        await viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);

        Assert.Equal(expectedBatchCount, executor.SelectedBatches.Count);
        Assert.All(executor.SelectedBatches, batch => Assert.InRange(batch.Count, 1, 500));
        Assert.Equal(orderGuids, executor.SelectedBatches.SelectMany(batch => batch));
        Assert.Equal(orderCount, confirmation.OrderCount);
        Assert.Equal(expectedBatchCount, confirmation.BatchCount);
    }

    [Fact]
    public async Task Reupload_date_range_with_1201_orders_executes_500_500_201_and_ignores_search_text()
    {
        var orderGuids = Enumerable.Range(0, 1201).Select(_ => Guid.NewGuid()).ToArray();
        var executor = new CallbackOrderExecutor(
            ids => new OrderUploadExecutionResult(ids.Count, ids.Count, 0),
            orderGuids);
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor,
            confirmationDialogService: new CapturingConfirmationDialogService { Result = true })
        {
            DateFrom = new DateTime(2026, 7, 1),
            DateTo = new DateTime(2026, 7, 2),
            SearchText = "ignored search term"
        };

        await viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);

        Assert.Equal([500, 500, 201], executor.SelectedBatches.Select(batch => batch.Count));
        Assert.Equal(
            new DateTimeOffset(new DateTime(2026, 7, 1)),
            executor.LastReuploadableQuery?.SoldFrom);
        Assert.Equal(
            new DateTimeOffset(new DateTime(2026, 7, 3).AddTicks(-1)),
            executor.LastReuploadableQuery?.SoldTo);
        Assert.Equal("POS-01", executor.LastReuploadableQuery?.DeviceCode);
    }

    [Fact]
    public async Task Reupload_date_range_stops_before_the_next_batch_when_endpoint_switch_interrupts()
    {
        var batchNumber = 0;
        var orderGuids = Enumerable.Range(0, 1201).Select(_ => Guid.NewGuid()).ToArray();
        var executor = new CallbackOrderExecutor(
            ids =>
            {
                batchNumber++;
                return batchNumber == 2
                    ? new OrderUploadExecutionResult(ids.Count, 300, 200, WasInterrupted: true)
                    : new OrderUploadExecutionResult(ids.Count, ids.Count, 0);
            },
            orderGuids);
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor,
            confirmationDialogService: new CapturingConfirmationDialogService { Result = true });

        await viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);

        Assert.Equal([500, 500], executor.SelectedBatches.Select(batch => batch.Count));
        Assert.Contains("server address was switching", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reupload_date_range_aggregates_partial_failures_across_batches()
    {
        var batchNumber = 0;
        var orderGuids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        var executor = new CallbackOrderExecutor(
            ids =>
            {
                batchNumber++;
                return batchNumber == 1
                    ? new OrderUploadExecutionResult(ids.Count, 498, 2)
                    : new OrderUploadExecutionResult(ids.Count, 0, 1);
            },
            orderGuids);
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor,
            confirmationDialogService: new CapturingConfirmationDialogService { Result = true });

        await viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);

        Assert.Contains("501 attempted, 498 succeeded, 3 failed", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reupload_date_range_resets_busy_state_when_a_later_batch_throws()
    {
        var batchNumber = 0;
        var orderGuids = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        var executor = new CallbackOrderExecutor(
            ids =>
            {
                batchNumber++;
                return batchNumber == 2
                    ? throw new InvalidOperationException("second batch failed")
                    : new OrderUploadExecutionResult(ids.Count, ids.Count, 0);
            },
            orderGuids);
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor,
            confirmationDialogService: new CapturingConfirmationDialogService { Result = true });

        await viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);

        Assert.Equal([500, 500], executor.SelectedBatches.Select(batch => batch.Count));
        Assert.False(viewModel.IsReuploading);
        Assert.True(viewModel.ReuploadDateRangeCommand.CanExecute(null));
        Assert.Contains("second batch failed", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reupload_date_range_does_not_upload_when_confirmation_is_cancelled_or_no_orders_match()
    {
        var orderGuids = new[] { Guid.NewGuid() };
        var executor = new CallbackOrderExecutor(
            ids => new OrderUploadExecutionResult(ids.Count, ids.Count, 0),
            orderGuids);
        var confirmation = new CapturingConfirmationDialogService { Result = false };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor,
            confirmationDialogService: confirmation)
        {
            DateFrom = new DateTime(2026, 7, 1),
            DateTo = new DateTime(2026, 7, 1)
        };

        await viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);

        Assert.Empty(executor.SelectedBatches);
        Assert.Equal(1, confirmation.CallCount);

        executor.ReuploadableOrderGuids = [];
        confirmation.Result = true;
        await viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.CallCount);
        Assert.Empty(executor.SelectedBatches);
    }

    [Fact]
    public async Task Reupload_date_range_disables_other_reupload_actions_until_the_current_batch_finishes()
    {
        var completion = new TaskCompletionSource<OrderUploadExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new CallbackOrderExecutor(
            ids => new OrderUploadExecutionResult(ids.Count, ids.Count, 0),
            [Guid.NewGuid()])
        {
            SelectedExecutionCompletion = completion
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            null,
            null,
            CreateSession(),
            orderUploadExecutionService: executor,
            confirmationDialogService: new CapturingConfirmationDialogService { Result = true })
        {
            DateFrom = new DateTime(2026, 7, 1),
            DateTo = new DateTime(2026, 7, 1)
        };

        var reuploadTask = viewModel.ReuploadDateRangeCommand.ExecuteAsync(null);
        await executor.SelectedExecutionStarted.Task;

        Assert.True(viewModel.IsReuploading);
        Assert.False(viewModel.ReuploadDateRangeCommand.CanExecute(null));
        Assert.False(viewModel.ReuploadSelectedCommand.CanExecute(null));
        Assert.False(viewModel.SelectAllReuploadableCommand.CanExecute(null));

        completion.SetResult(new OrderUploadExecutionResult(1, 1, 0));
        await reuploadTask;

        Assert.False(viewModel.IsReuploading);
    }

    [Fact]
    public void Constructor_initializes_readonly_store_and_terminal_dropdown()
    {
        var session = CreateSession(deviceCode: "POS-09");
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            session);

        Assert.Equal("Main Store (S001)", viewModel.StoreFilterText);
        Assert.Collection(
            viewModel.TerminalOptions,
            option =>
            {
                Assert.Null(option.DeviceCode);
                Assert.Equal("All Terminals", option.Label);
            },
            option =>
            {
                Assert.Equal("POS-09", option.DeviceCode);
                Assert.Equal("POS-09", option.Label);
            });
        Assert.Equal("POS-09", viewModel.SelectedTerminalOption?.DeviceCode);
        Assert.Equal("POS-09", viewModel.TerminalFilterText);
        Assert.True(viewModel.IsLocalSourceSelected);
        Assert.False(viewModel.IsOnlineSourceSelected);
        Assert.True(viewModel.IsStandardSourceSelected);
    }

    [Fact]
    public void Session_change_preserves_all_terminal_selection_and_refreshes_current_terminal_option()
    {
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(deviceCode: "POS-01"));
        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode is null);

        viewModel.Session = CreateSession(deviceCode: "POS-02", storeCode: "S002", storeName: "Second Store");

        Assert.Equal("Second Store (S002)", viewModel.StoreFilterText);
        Assert.Null(viewModel.SelectedTerminalOption?.DeviceCode);
        Assert.Equal("All Terminals", viewModel.TerminalFilterText);
        Assert.Contains(viewModel.TerminalOptions, option => option.DeviceCode == "POS-02");
    }

    [Fact]
    public async Task LoadAsync_passes_selected_terminal_filter_to_all_history_sources()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var suspendedOrders = new CapturingSuspendedOrderService();
        var remoteOrders = new CapturingRemoteOrderHistoryService();
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            suspendedOrders,
            remoteOrders,
            CreateSession(deviceCode: "POS-04"));

        await viewModel.LoadAsync();
        Assert.Equal("POS-04", receiptQuery.LastQuery?.DeviceCode);
        Assert.Equal("POS-04", suspendedOrders.LastDeviceCode);

        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode is null);
        await viewModel.LoadAsync();
        Assert.Null(receiptQuery.LastQuery?.DeviceCode);
        Assert.Null(suspendedOrders.LastDeviceCode);

        viewModel.IsOnlineSourceSelected = true;
        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode == "POS-04");
        await viewModel.LoadAsync();
        Assert.Equal("POS-04", remoteOrders.LastQuery?.DeviceCode);

        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode is null);
        await viewModel.LoadAsync();
        Assert.Null(remoteOrders.LastQuery?.DeviceCode);
    }

    [Fact]
    public async Task LoadAsync_handles_remote_timeout_without_throwing()
    {
        const string timeoutMessage = "The request timed out.";
        var remoteOrders = new CapturingRemoteOrderHistoryService();
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession());
        viewModel.IsOnlineSourceSelected = true;
        remoteOrders.QueryException = new TaskCanceledException(timeoutMessage);

        var exception = await Record.ExceptionAsync(() => viewModel.LoadAsync());

        Assert.Null(exception);
        Assert.Empty(viewModel.Orders);
        Assert.Equal(timeoutMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task LoadAsync_propagates_caller_cancellation()
    {
        var remoteOrders = new CapturingRemoteOrderHistoryService();
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession());
        viewModel.IsOnlineSourceSelected = true;
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        remoteOrders.QueryException = new OperationCanceledException(cancellationSource.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.LoadAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task Local_history_merges_local_and_suspended_orders_and_sorts_descending()
    {
        var localOrderGuid = Guid.NewGuid();
        var suspendedOrderGuid = Guid.NewGuid();
        var receiptQuery = new CapturingReceiptQueryService
        {
            Orders =
            [
                new LocalOrderSummary(
                    localOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero),
                    16m,
                    1m,
                    15m,
                    "Synced",
                    2,
                    "Cash")
            ]
        };
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    suspendedOrderGuid,
                    "S001",
                    "POS-02",
                    "Bob",
                    new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero),
                    12m,
                    0m,
                    12m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession())
        {
            DateFrom = new DateTime(2026, 5, 10),
            DateTo = new DateTime(2026, 5, 10)
        };

        await viewModel.LoadAsync();

        Assert.Collection(
            viewModel.Orders,
            order =>
            {
                Assert.Equal(suspendedOrderGuid, order.OrderGuid);
                Assert.True(order.IsSuspendedOrder);
                Assert.True(order.CanRecall);
                Assert.Equal("Suspended", order.PaymentSummary);
                Assert.Equal("Pending recall", order.StatusLabel);
            },
            order =>
            {
                Assert.Equal(localOrderGuid, order.OrderGuid);
                Assert.False(order.IsSuspendedOrder);
                Assert.False(order.CanRecall);
                Assert.Equal("Cash", order.PaymentSummary);
            });
    }

    [Fact]
    public async Task LoadAsync_applies_date_range_to_local_and_suspended_orders()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    Guid.NewGuid(),
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 3, 9, 0, 0, TimeSpan.Zero),
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending),
                new SuspendedOrderSummary(
                    Guid.NewGuid(),
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 7, 9, 0, 0, TimeSpan.Zero),
                    20m,
                    0m,
                    20m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession())
        {
            DateFrom = new DateTime(2026, 5, 5),
            DateTo = new DateTime(2026, 5, 6)
        };

        await viewModel.LoadAsync();

        Assert.Equal(new DateTime(2026, 5, 5), receiptQuery.LastQuery?.SoldFrom?.Date);
        Assert.Equal(new DateTime(2026, 5, 6), receiptQuery.LastQuery?.SoldTo?.Date);
        Assert.Empty(viewModel.Orders);
    }

    [Fact]
    public async Task Recall_order_command_uses_row_parameter_refreshes_list_and_invokes_callback()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var recalled = false;
        var suspendedOrderGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    suspendedOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.Now,
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            () =>
            {
                recalled = true;
                return Task.CompletedTask;
            },
            operationAuditLogger: auditLogger);

        await viewModel.LoadAsync();
        var suspendedOrder = Assert.Single(viewModel.Orders);

        Assert.True(viewModel.RecallOrderCommand.CanExecute(suspendedOrder));

        await viewModel.RecallOrderCommand.ExecuteAsync(suspendedOrder);

        Assert.True(recalled);
        Assert.Equal(suspendedOrderGuid, suspendedOrders.RecalledOrderGuid);
        Assert.Empty(viewModel.Orders);
        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("ORDER_RECALL", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal(suspendedOrderGuid.ToString("D"), auditEvent.OrderGuid);
        Assert.Equal(0m, auditEvent.BeforeActual);
        Assert.Equal(10m, auditEvent.AfterActual);
        Assert.Equal(10m, auditEvent.AmountDelta);
    }

    [Fact]
    public async Task Selected_suspended_order_shows_recall_action_and_hides_reprint()
    {
        var suspendedOrderGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    suspendedOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.Now,
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending)
            ],
            Orders =
            {
                [suspendedOrderGuid] = new SuspendedOrder(
                    suspendedOrderGuid,
                    "S001",
                    "POS-01",
                    "C001",
                    "Alice",
                    DateTimeOffset.Now,
                    10m,
                    0m,
                    10m,
                    SuspendedOrderStatus.Pending,
                    [
                        new SuspendedOrderLine(
                            Guid.NewGuid(),
                            suspendedOrderGuid,
                            "S001",
                            "SKU-HOLD",
                            null,
                            "Held Tea",
                            "BAR-HOLD",
                            "ITEM-HOLD",
                            "snapshot://held-image",
                            1m,
                            10m,
                            0m,
                            null,
                            10m,
                            PriceSourceKind.ProductBase,
                            "Product")
                    ])
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession());

        await viewModel.ShowSuspendedOrdersAsync();

        // 收银页“取回”入口应直接落在挂单页签的本机范围，而不是本机销售记录。
        Assert.True(viewModel.IsHeldSourceSelected);
        Assert.True(viewModel.IsHeldLocalScopeSelected);
        Assert.True(viewModel.IsRecallVisible);
        Assert.False(viewModel.IsReprintVisible);
        Assert.True(viewModel.RecallSelectedCommand.CanExecute(null));
        Assert.Equal(suspendedOrderGuid, viewModel.SelectedOrder?.OrderGuid);
        Assert.True(viewModel.SelectedOrder?.CanRecall);
        Assert.Contains(
            viewModel.ReceiptPreviewRows,
            row => row.Text.Equals("*** Suspended ***", StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.ReceiptPreviewRows,
            row => row.Text.Equals("*** Paid ***", StringComparison.Ordinal));
        Assert.Equal(
            suspendedOrderGuid.ToString("D"),
            Assert.Single(viewModel.ReceiptPreviewRows, row => row.IsQrCode).QrCodeValue);
        Assert.True(viewModel.SelectedOrder?.CanViewOrderDetails);
        var detailLine = Assert.Single(viewModel.OrderDetailLinesForTests);
        Assert.Equal("SKU-HOLD", detailLine.ProductCode);
        Assert.Equal("ITEM-HOLD", detailLine.ItemNumber);
        Assert.Equal("BAR-HOLD", detailLine.LookupCode);
        Assert.Equal("snapshot://held-image", detailLine.ProductImage);
        Assert.False(viewModel.HasOrderDetailsPayments);
    }

    [Fact]
    public async Task Entering_history_resets_stale_date_range_to_today()
    {
        // 视图模型常驻复用：模拟收银机前一天打开过历史页、次日未重启再次进入。
        var today = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            timeProvider: new FixedUtcTimeProvider(today))
        {
            DateFrom = new DateTime(2026, 9, 28),
            DateTo = new DateTime(2026, 9, 28)
        };

        await viewModel.ShowSuspendedOrdersAsync();

        Assert.Equal(today.Date, viewModel.DateFrom);
        Assert.Equal(today.Date, viewModel.DateTo);

        viewModel.DateFrom = new DateTime(2026, 9, 1);
        viewModel.DateTo = new DateTime(2026, 9, 2);
        viewModel.ResetDateRangeToToday();

        Assert.Equal(today.Date, viewModel.DateFrom);
        Assert.Equal(today.Date, viewModel.DateTo);
    }

    [Fact]
    public async Task Remote_history_shows_reprint_and_hides_recall()
    {
        var orderGuid = Guid.NewGuid();
        var remoteReceipt = new ReceiptDetails(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.Now,
            12m,
            0m,
            12m,
            [new ReceiptPreviewLine("Remote Tea", "930002", 1m, 12m, 0m, 12m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 12m, null)]);
        var reprintRequested = false;
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService
            {
                QueryResult = new RemoteOrderHistoryResult(
                [
                    new RemoteOrderHistorySummary(
                        orderGuid,
                        "S001",
                        "POS-01",
                        "Alice",
                        DateTimeOffset.Now,
                        12m,
                        0m,
                        12m,
                        1,
                        "Cash",
                        "Synced")
                ]),
                Receipts =
                {
                    [orderGuid] = remoteReceipt
                }
            },
            CreateSession());
        viewModel.ReprintRequested += (_, _) => reprintRequested = true;

        viewModel.IsOnlineSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.False(viewModel.IsRecallVisible);
        Assert.True(viewModel.IsReprintVisible);
        Assert.False(viewModel.RecallSelectedCommand.CanExecute(null));
        Assert.True(viewModel.ReprintCommand.CanExecute(null));
        Assert.Same(remoteReceipt, viewModel.SelectedReceipt);
        Assert.Equal(
            orderGuid.ToString("D"),
            Assert.Single(viewModel.ReceiptPreviewRows, row => row.IsQrCode).QrCodeValue);

        var selectedOrder = Assert.Single(viewModel.Orders);
        Assert.False(viewModel.IsReceiptPreviewOpen);
        Assert.True(viewModel.OpenReceiptPreviewCommand.CanExecute(selectedOrder));

        viewModel.OpenReceiptPreviewCommand.Execute(selectedOrder);

        Assert.True(viewModel.IsReceiptPreviewOpen);

        viewModel.CloseReceiptPreviewCommand.Execute(null);

        Assert.False(viewModel.IsReceiptPreviewOpen);

        viewModel.ReprintCommand.Execute(null);

        Assert.True(reprintRequested);
    }

    [Fact]
    public async Task Remote_history_without_receipt_details_hides_reprint()
    {
        var orderGuid = Guid.NewGuid();
        var reprintRequested = false;
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService
            {
                QueryResult = new RemoteOrderHistoryResult(
                [
                    new RemoteOrderHistorySummary(
                        orderGuid,
                        "S001",
                        "POS-01",
                        "Alice",
                        DateTimeOffset.Now,
                        12m,
                        0m,
                        12m,
                        1,
                        "Cash",
                        "Synced")
                ])
            },
            CreateSession());
        viewModel.ReprintRequested += (_, _) => reprintRequested = true;

        viewModel.IsOnlineSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.Null(viewModel.SelectedReceipt);
        Assert.False(viewModel.IsReprintVisible);
        Assert.False(viewModel.ReprintCommand.CanExecute(null));

        viewModel.ReprintCommand.Execute(null);

        Assert.False(reprintRequested);
    }

    [Fact]
    public async Task Source_switch_blocks_reprint_while_new_source_loads()
    {
        var orderGuid = Guid.NewGuid();
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryGate = new TaskCompletionSource<RemoteOrderHistoryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reprintRequested = false;
        var receiptQuery = new CapturingReceiptQueryService
        {
            Orders =
            [
                new LocalOrderSummary(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.Now,
                    5m,
                    0m,
                    5m,
                    "Synced",
                    1,
                    "Cash")
            ],
            Receipts =
            {
                [orderGuid] = new ReceiptDetails(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.Now,
                    5m,
                    0m,
                    5m,
                    [new ReceiptPreviewLine("Receipt Tea", "930001", 1m, 5m, 0m, 5m)],
                    [new ReceiptPaymentLine(PaymentMethodKind.Cash, 5m, null)])
            }
        };
        var remoteOrders = new CapturingRemoteOrderHistoryService
        {
            QueryHandler = (_, _) =>
            {
                queryStarted.TrySetResult();
                return queryGate.Task;
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession());
        viewModel.ReprintRequested += (_, _) => reprintRequested = true;

        await viewModel.LoadAsync();
        Assert.True(viewModel.ReprintCommand.CanExecute(null));

        viewModel.IsOnlineSourceSelected = true;
        await queryStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal(orderGuid, viewModel.SelectedOrder?.OrderGuid);
        Assert.Equal(orderGuid, viewModel.SelectedReceipt?.OrderGuid);
        Assert.False(viewModel.IsReprintVisible);
        Assert.False(viewModel.ReprintCommand.CanExecute(null));

        viewModel.ReprintCommand.Execute(null);
        Assert.False(reprintRequested);

        queryGate.SetResult(new RemoteOrderHistoryResult([]));
        await viewModel.LoadAsync();
    }

    [Fact]
    public async Task Remote_history_ignores_stale_receipt_after_selection_changes()
    {
        var firstOrderGuid = Guid.NewGuid();
        var secondOrderGuid = Guid.NewGuid();
        var soldAt = DateTimeOffset.Now;
        var firstReceipt = new ReceiptDetails(
            firstOrderGuid,
            "S001",
            "POS-01",
            "Alice",
            soldAt,
            12m,
            0m,
            12m,
            [new ReceiptPreviewLine("First Item", "930002", 1m, 12m, 0m, 12m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 12m, null)]);
        var secondReceipt = new ReceiptDetails(
            secondOrderGuid,
            "S001",
            "POS-02",
            "Bob",
            soldAt.AddMinutes(-1),
            8m,
            0m,
            8m,
            [new ReceiptPreviewLine("Second Item", "930003", 1m, 8m, 0m, 8m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 8m, null)]);
        var firstReceiptGate = new TaskCompletionSource<ReceiptDetails?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteOrders = new CapturingRemoteOrderHistoryService
        {
            QueryResult = new RemoteOrderHistoryResult(
            [
                new RemoteOrderHistorySummary(
                    firstOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    soldAt,
                    12m,
                    0m,
                    12m,
                    1,
                    "Cash",
                    "Synced"),
                new RemoteOrderHistorySummary(
                    secondOrderGuid,
                    "S001",
                    "POS-02",
                    "Bob",
                    soldAt.AddMinutes(-1),
                    8m,
                    0m,
                    8m,
                    1,
                    "Cash",
                    "Synced")
            ])
        };
        remoteOrders.DetailsHandler = (orderGuid, _) =>
        {
            if (orderGuid == firstOrderGuid)
            {
                firstRequestStarted.TrySetResult();
                return firstReceiptGate.Task;
            }

            return Task.FromResult<ReceiptDetails?>(
                orderGuid == secondOrderGuid ? secondReceipt : null);
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession());

        viewModel.IsOnlineSourceSelected = true;
        var initialLoad = viewModel.LoadAsync();
        await firstRequestStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        viewModel.SelectedOrder = Assert.Single(
            viewModel.Orders,
            order => order.OrderGuid == secondOrderGuid);
        await WaitUntilAsync(() => ReferenceEquals(viewModel.SelectedReceipt, secondReceipt));

        firstReceiptGate.SetResult(firstReceipt);
        await initialLoad;

        Assert.Equal(secondOrderGuid, viewModel.SelectedOrder?.OrderGuid);
        Assert.Same(secondReceipt, viewModel.SelectedReceipt);
        Assert.True(viewModel.IsReprintVisible);
        Assert.Equal(
            secondOrderGuid.ToString("D"),
            Assert.Single(viewModel.ReceiptPreviewRows, row => row.IsQrCode).QrCodeValue);
        Assert.Equal("Second Item", Assert.Single(viewModel.OrderDetailLinesForTests).DisplayName);
    }

    [Fact]
    public async Task Remote_history_ignores_older_request_when_selection_returns_to_same_order()
    {
        var firstOrderGuid = Guid.NewGuid();
        var secondOrderGuid = Guid.NewGuid();
        var soldAt = DateTimeOffset.Now;
        var firstOrder = new HistoryOrderListItem(
            firstOrderGuid,
            TransactionHistorySource.RemoteOrders,
            "S001",
            "POS-01",
            "Alice",
            soldAt,
            12m,
            0m,
            12m,
            1,
            "Cash",
            "Synced");
        var secondOrder = new HistoryOrderListItem(
            secondOrderGuid,
            TransactionHistorySource.RemoteOrders,
            "S001",
            "POS-02",
            "Bob",
            soldAt.AddMinutes(-1),
            8m,
            0m,
            8m,
            1,
            "Cash",
            "Synced");
        var staleFirstReceipt = new ReceiptDetails(
            firstOrderGuid,
            "S001",
            "POS-01",
            "Alice",
            soldAt,
            12m,
            0m,
            12m,
            [new ReceiptPreviewLine("Stale first item", "930002", 1m, 12m, 0m, 12m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 12m, null)]);
        var currentFirstReceipt = staleFirstReceipt with
        {
            Lines = [new ReceiptPreviewLine("Current first item", "930004", 1m, 12m, 0m, 12m)]
        };
        var firstRequestGate = new TaskCompletionSource<ReceiptDetails?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestGate = new TaskCompletionSource<ReceiptDetails?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var currentRequestGate = new TaskCompletionSource<ReceiptDetails?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var currentRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOrderRequestCount = 0;
        var remoteOrders = new CapturingRemoteOrderHistoryService();
        remoteOrders.DetailsHandler = (orderGuid, _) =>
        {
            if (orderGuid == secondOrderGuid)
            {
                secondRequestStarted.TrySetResult();
                return secondRequestGate.Task;
            }

            if (Interlocked.Increment(ref firstOrderRequestCount) == 1)
            {
                firstRequestStarted.TrySetResult();
                return firstRequestGate.Task;
            }

            currentRequestStarted.TrySetResult();
            return currentRequestGate.Task;
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession());

        viewModel.SelectedOrder = firstOrder;
        await firstRequestStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        viewModel.SelectedOrder = secondOrder;
        await secondRequestStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        viewModel.SelectedOrder = firstOrder;
        await currentRequestStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        firstRequestGate.SetResult(staleFirstReceipt);
        await Task.Delay(100);

        Assert.Null(viewModel.SelectedReceipt);
        Assert.True(viewModel.IsReceiptPreviewLoading);

        secondRequestGate.SetResult(null);
        currentRequestGate.SetResult(currentFirstReceipt);
        await WaitUntilAsync(() =>
            ReferenceEquals(viewModel.SelectedReceipt, currentFirstReceipt) &&
            !viewModel.IsReceiptPreviewLoading);

        Assert.False(viewModel.IsReceiptPreviewLoading);
        Assert.Equal("Current first item", Assert.Single(viewModel.OrderDetailLinesForTests).DisplayName);
    }

    [Fact]
    public async Task Remote_history_load_does_not_wait_for_first_detail_and_preserves_rows()
    {
        var orderGuid = Guid.NewGuid();
        var detailsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailsGate = new TaskCompletionSource<ReceiptDetails?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteOrders = new CapturingRemoteOrderHistoryService
        {
            QueryResult = new RemoteOrderHistoryResult(
            [
                new RemoteOrderHistorySummary(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.UtcNow,
                    12m,
                    0m,
                    12m,
                    1,
                    "Cash",
                    "Synced")
            ])
        };
        remoteOrders.DetailsHandler = (_, _) =>
        {
            detailsStarted.TrySetResult();
            return detailsGate.Task;
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession());

        viewModel.IsOnlineSourceSelected = true;
        var loadTask = viewModel.LoadAsync();
        await detailsStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        try
        {
            var completed = await Task.WhenAny(loadTask, Task.Delay(AsyncTestWaitSupport.DefaultTimeout));

            Assert.Same(loadTask, completed);
            Assert.Equal(orderGuid, Assert.Single(viewModel.Orders).OrderGuid);
        }
        finally
        {
            detailsGate.TrySetResult(null);
            await loadTask;
        }
    }

    [Fact]
    public async Task Remote_history_detail_failure_does_not_clear_loaded_rows()
    {
        const string detailFailure = "remote detail failed";
        var orderGuid = Guid.NewGuid();
        var remoteOrders = new CapturingRemoteOrderHistoryService
        {
            QueryResult = new RemoteOrderHistoryResult(
            [
                new RemoteOrderHistorySummary(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.UtcNow,
                    12m,
                    0m,
                    12m,
                    1,
                    "Cash",
                    "Synced")
            ]),
            DetailsHandler = (_, _) => Task.FromException<ReceiptDetails?>(new InvalidOperationException(detailFailure))
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession());

        viewModel.IsOnlineSourceSelected = true;
        await viewModel.LoadAsync();
        await WaitUntilAsync(() => viewModel.StatusMessage == detailFailure);

        Assert.Equal(orderGuid, Assert.Single(viewModel.Orders).OrderGuid);
        Assert.Null(viewModel.SelectedReceipt);
        Assert.False(viewModel.ReprintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Remote_history_selection_handles_details_timeout_without_unobserved_exception()
    {
        var firstOrderGuid = Guid.NewGuid();
        var secondOrderGuid = Guid.NewGuid();
        var soldAt = DateTimeOffset.Now;
        var firstReceipt = new ReceiptDetails(
            firstOrderGuid,
            "S001",
            "POS-01",
            "Alice",
            soldAt,
            12m,
            0m,
            12m,
            [new ReceiptPreviewLine("First Item", "930002", 1m, 12m, 0m, 12m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 12m, null)]);
        var remoteOrders = new CapturingRemoteOrderHistoryService
        {
            QueryResult = new RemoteOrderHistoryResult(
            [
                new RemoteOrderHistorySummary(
                    firstOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    soldAt,
                    12m,
                    0m,
                    12m,
                    1,
                    "Cash",
                    "Synced"),
                new RemoteOrderHistorySummary(
                    secondOrderGuid,
                    "S001",
                    "POS-02",
                    "Bob",
                    soldAt.AddMinutes(-1),
                    8m,
                    0m,
                    8m,
                    1,
                    "Cash",
                    "Synced")
            ])
        };
        remoteOrders.DetailsHandler = (orderGuid, _) => orderGuid == firstOrderGuid
            ? Task.FromResult<ReceiptDetails?>(firstReceipt)
            : Task.FromException<ReceiptDetails?>(new TaskCanceledException("request timeout"));
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            remoteOrders,
            CreateSession());

        viewModel.IsOnlineSourceSelected = true;
        await viewModel.LoadAsync();

        viewModel.SelectedOrder = Assert.Single(
            viewModel.Orders,
            order => order.OrderGuid == secondOrderGuid);
        await WaitUntilAsync(() => viewModel.StatusMessage == "Order details load timed out. Please try again.");

        Assert.Null(viewModel.SelectedReceipt);
        Assert.Empty(viewModel.ReceiptPreviewRows);
        Assert.False(viewModel.ReprintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Local_history_selection_builds_formatter_backed_preview_and_reprint_event()
    {
        var orderGuid = Guid.NewGuid();
        var reprintRequested = false;
        var receiptQuery = new CapturingReceiptQueryService
        {
            Orders =
            [
                new LocalOrderSummary(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 27, 9, 0, 0, TimeSpan.Zero),
                    5m,
                    0m,
                    5m,
                    "Synced",
                    1,
                    "Cash")
            ],
            Receipts =
            {
                [orderGuid] = new ReceiptDetails(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 27, 9, 0, 0, TimeSpan.Zero),
                    5m,
                    0m,
                    5m,
                    [new ReceiptPreviewLine("Receipt Tea", "930001", 1m, 5m, 0m, 5m)],
                    [new ReceiptPaymentLine(PaymentMethodKind.Cash, 5m, null)])
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession());
        viewModel.ReprintRequested += (_, _) => reprintRequested = true;

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsReprintVisible);
        Assert.True(viewModel.ReprintCommand.CanExecute(null));
        Assert.Contains(viewModel.ReceiptPreviewRows, row => row.Text.Contains("===== TAX INVOICE =====", StringComparison.Ordinal));
        Assert.Equal(
            orderGuid.ToString("D"),
            Assert.Single(viewModel.ReceiptPreviewRows, row => row.IsQrCode).QrCodeValue);

        viewModel.ReprintCommand.Execute(null);

        Assert.True(reprintRequested);
    }

    [Fact]
    public async Task Local_history_preview_time_uses_localization_culture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            var localization = new LocalizationService();
            var soldAt = new DateTimeOffset(2026, 7, 28, 5, 56, 0, TimeSpan.Zero);
            var orderGuid = Guid.NewGuid();
            var receiptQuery = new CapturingReceiptQueryService
            {
                Orders =
                [
                    new LocalOrderSummary(
                        orderGuid,
                        "S001",
                        "POS-01",
                        "Alice",
                        soldAt,
                        5m,
                        0m,
                        5m,
                        "Synced",
                        1,
                        "Cash")
                ],
                Receipts =
                {
                    [orderGuid] = new ReceiptDetails(
                        orderGuid,
                        "S001",
                        "POS-01",
                        "Alice",
                        soldAt,
                        5m,
                        0m,
                        5m,
                        [new ReceiptPreviewLine("Receipt Tea", "930001", 1m, 5m, 0m, 5m)],
                        [new ReceiptPaymentLine(PaymentMethodKind.Cash, 5m, null)])
                }
            };
            using var viewModel = new TransactionHistoryViewModel(
                receiptQuery,
                new CapturingSuspendedOrderService(),
                new CapturingRemoteOrderHistoryService(),
                CreateSession(),
                localization: localization);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(LocalizationService.ChineseCultureName);
            await viewModel.LoadAsync();

            Assert.Equal(
                soldAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm", localization.CurrentCulture),
                viewModel.PreviewSoldAt);
            Assert.DoesNotContain("\u6708", viewModel.PreviewSoldAt, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = originalDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Return_to_pos_command_invokes_callback()
    {
        var returned = false;
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            returnToPos: () => returned = true);

        Assert.True(viewModel.ReturnToPosCommand.CanExecute(null));

        viewModel.ReturnToPosCommand.Execute(null);

        Assert.True(returned);
    }

    [Fact]
    public async Task Suspended_order_labels_follow_localization_culture()
    {
        var suspendedOrderGuid = Guid.NewGuid();
        var localization = new LocalizationService();
        var localSoldAt = new DateTime(2026, 7, 28, 5, 56, 0, DateTimeKind.Unspecified);
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    suspendedOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(localSoldAt, TimeZoneInfo.Local.GetUtcOffset(localSoldAt)),
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            localization: localization)
        {
            DateFrom = localSoldAt.Date,
            DateTo = localSoldAt.Date
        };

        await viewModel.LoadAsync();
        Assert.Equal("Suspended", viewModel.SelectedOrder?.PaymentSummary);
        Assert.Equal("Local", viewModel.SourceOptions[0].Label);
        Assert.Equal("Jul 28, 2026 05:56", viewModel.SelectedOrder?.SoldAtDisplay);

        localization.SetCulture("zh-CN");

        Assert.Equal("\u6302\u5355", viewModel.SelectedOrder?.PaymentSummary);
        Assert.Equal("\u5F85\u53D6\u56DE", viewModel.SelectedOrder?.StatusLabel);
        Assert.Equal("\u672C\u5730", viewModel.SourceOptions[0].Label);
        Assert.Contains("\u6708", viewModel.SelectedOrder?.SoldAtDisplay);

        localization.SetCulture("en-US");

        Assert.Equal("Jul 28, 2026 05:56", viewModel.SelectedOrder?.SoldAtDisplay);
    }

    [Fact]
    public void Date_picker_language_follows_localization_culture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            var localization = new LocalizationService();
            using var viewModel = new TransactionHistoryViewModel(
                new CapturingReceiptQueryService(),
                new CapturingSuspendedOrderService(),
                new CapturingRemoteOrderHistoryService(),
                CreateSession(),
                localization: localization);
            var changedProperties = new List<string?>();
            viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

            Assert.Equal(
                LocalizationService.DefaultCultureName,
                viewModel.CurrentUiLanguage.IetfLanguageTag,
                ignoreCase: true);

            localization.SetCulture(LocalizationService.ChineseCultureName);

            Assert.Equal(
                LocalizationService.ChineseCultureName,
                viewModel.CurrentUiLanguage.IetfLanguageTag,
                ignoreCase: true);
            Assert.Contains(nameof(TransactionHistoryViewModel.CurrentUiLanguage), changedProperties);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = originalDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Dispose_is_idempotent_and_stops_culture_updates()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            localization: localization);
        var changeCount = 0;
        viewModel.PropertyChanged += (_, _) => changeCount++;

        try
        {
            viewModel.Dispose();
            viewModel.Dispose();
            localization.SetCulture(LocalizationService.ChineseCultureName);

            Assert.Equal(0, changeCount);
        }
        finally
        {
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }

    [Fact]
    public void All_terminal_filter_text_uses_current_culture_when_selected()
    {
        var localization = new LocalizationService();
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(deviceCode: "POS-01"),
            localization: localization);
        localization.SetCulture("zh-CN");

        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode is null);

        Assert.Equal("\u5168\u90E8\u7EC8\u7AEF", viewModel.TerminalFilterText);
    }

    [Fact]
    public async Task Installment_history_labels_follow_localization_culture_without_duplicate_paid_label()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var order = CreateInstallmentOrder(
            "IO-20260826-I18N",
            "Customer",
            "0400111222",
            paidAmount: 50m,
            outstandingAmount: 38m);
        using var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: new CapturingInstallmentOrderService { Orders = [order] },
            localization: localization);
        viewModel.IsInstallmentSourceSelected = true;

        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Orders);
        Assert.Equal("$50.00", row.PaymentSummary);
        Assert.Equal("Payment due", row.StatusLabel);

        localization.SetCulture(LocalizationService.ChineseCultureName);

        row = Assert.Single(viewModel.Orders);
        Assert.Equal("$50.00", row.PaymentSummary);
        Assert.Equal("\u5F85\u8865\u6B3E", row.StatusLabel);

        localization.SetCulture(LocalizationService.DefaultCultureName);

        Assert.Equal("Payment due", Assert.Single(viewModel.Orders).StatusLabel);
    }

    [Fact]
    public async Task Installment_history_source_loads_orders_and_continues_payment()
    {
        var order = CreateInstallmentOrder("IO-20260703-0001", "张三", "0400111222", paidAmount: 30m, outstandingAmount: 90m);
        var localOrder = CreateLocalInstallmentOrder(order);
        InstallmentOrderSummary? continuedOrder = null;
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: new CapturingInstallmentOrderService
            {
                Orders = [order],
                LocalOrders = { [order.OrderId] = localOrder }
            },
            continueInstallmentPaymentAsync: selected =>
            {
                continuedOrder = selected;
                return Task.CompletedTask;
            });

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.False(viewModel.IsStandardSourceSelected);
        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.IsInstallmentOrder);
        Assert.Equal(order.OrderNumber, row.DisplayOrderId);
        Assert.Equal(order.CustomerName, row.CashierName);
        Assert.Equal(order.OutstandingAmount, row.ActualAmount);
        Assert.True(row.CanContinueInstallmentPayment);
        Assert.True(viewModel.IsContinueInstallmentPaymentVisible);
        Assert.True(viewModel.ContinueInstallmentPaymentCommand.CanExecute(row));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains(order.OrderNumber, StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains(order.CustomerName, StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("TAX INVOICE", StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("Receipt Tea", StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("Cash", StringComparison.Ordinal) && preview.Text.Contains("$30.00", StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("Balance due", StringComparison.Ordinal) && preview.Text.Contains("$90.00", StringComparison.Ordinal));
        var detailLine = Assert.Single(viewModel.OrderDetailLinesForTests);
        Assert.Equal("P001", detailLine.ProductCode);
        Assert.Equal("ITEM-INSTALLMENT", detailLine.ItemNumber);
        Assert.Equal("930001", detailLine.LookupCode);
        Assert.Equal(
            order.OrderId.ToString("D"),
            Assert.Single(viewModel.ReceiptPreviewRows, preview => preview.IsQrCode).QrCodeValue);

        await viewModel.ContinueInstallmentPaymentCommand.ExecuteAsync(row);

        Assert.Same(order, continuedOrder);
    }

    [Fact]
    public async Task Installment_history_online_preview_uses_authoritative_detail_instead_of_local_snapshot()
    {
        var order = CreateInstallmentOrder("IO-20260703-AUTHORITATIVE", "张三", "0400111222", paidAmount: 30m, outstandingAmount: 90m);
        var staleLocalOrder = CreateLocalInstallmentOrder(order);
        var authoritativeOrder = staleLocalOrder with
        {
            PaidAmount = 80m,
            BalanceAmount = 40m,
            CustomerName = string.Empty,
            CustomerPhone = "0499999999",
            Lines =
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-AUTHORITATIVE",
                    null,
                    "Authoritative Item",
                    "BAR-AUTHORITATIVE",
                    1m,
                    120m,
                    0m,
                    120m,
                    "ITEM-AUTHORITATIVE")
            ]
        };
        var installmentService = new CapturingInstallmentOrderService
        {
            Orders = [order],
            LocalOrders = { [order.OrderId] = staleLocalOrder },
            OrderDetails = authoritativeOrder with
            {
                Payments =
                [
                    new InstallmentPaymentDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Cash,
                        80m,
                        null,
                        InstallmentPaymentStatus.Recorded,
                        DateTimeOffset.UtcNow,
                        "C001",
                        "POS-01")
                ]
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: installmentService);

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();
        await WaitUntilAsync(() => viewModel.SelectedReceipt is not null);

        Assert.Contains(viewModel.ReceiptLines, line => line.DisplayName == "Authoritative Item");
        Assert.DoesNotContain(viewModel.ReceiptLines, line => line.DisplayName == "Receipt Tea");
        Assert.Equal(80m, Assert.Single(viewModel.Payments).Amount);
        Assert.Equal(80m, viewModel.OrderDetailsPaidAmount);
        Assert.Equal(40m, viewModel.OrderDetailsOutstandingAmount);
        Assert.True(viewModel.HasOrderDetailsCustomer);
        Assert.Equal("-", viewModel.OrderDetailsCustomerName);
        Assert.Equal("0499999999", viewModel.OrderDetailsCustomerPhone);
        Assert.True(viewModel.ReprintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Installment_history_load_does_not_wait_for_authoritative_detail()
    {
        var order = CreateInstallmentOrder(
            "IO-20260703-DETAIL-GATE",
            "张三",
            "0400111222",
            paidAmount: 30m,
            outstandingAmount: 90m);
        var detailsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailsGate = new TaskCompletionSource<LocalInstallmentOrder?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var installmentService = new CapturingInstallmentOrderService
        {
            Orders = [order],
            OrderDetailsHandler = (_, _, _) =>
            {
                detailsStarted.TrySetResult();
                return detailsGate.Task;
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: installmentService);

        viewModel.IsInstallmentSourceSelected = true;
        var loadTask = viewModel.LoadAsync();
        await detailsStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        try
        {
            var completed = await Task.WhenAny(loadTask, Task.Delay(AsyncTestWaitSupport.DefaultTimeout));

            Assert.Same(loadTask, completed);
            Assert.Equal(order.OrderId, Assert.Single(viewModel.Orders).OrderGuid);
        }
        finally
        {
            detailsGate.TrySetResult(null);
            await loadTask;
        }
    }

    [Fact]
    public async Task Installment_history_online_detail_failure_does_not_reuse_stale_snapshot_or_enable_reprint()
    {
        const string detailFailure = "authoritative detail unavailable";
        var order = CreateInstallmentOrder("IO-20260703-FAIL-CLOSED", "张三", "0400111222", paidAmount: 30m, outstandingAmount: 90m);
        var staleLocalOrder = CreateLocalInstallmentOrder(order);
        var installmentService = new CapturingInstallmentOrderService
        {
            Orders = [order],
            LocalOrders = { [order.OrderId] = staleLocalOrder },
            OrderDetailsException = new InvalidOperationException(detailFailure)
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: installmentService);

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();
        await WaitUntilAsync(() => viewModel.StatusMessage == detailFailure);

        Assert.Single(viewModel.Orders);
        Assert.Null(viewModel.SelectedReceipt);
        Assert.Empty(viewModel.ReceiptLines);
        Assert.False(viewModel.ReprintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Installment_history_offline_preview_still_uses_complete_local_snapshot()
    {
        var order = CreateInstallmentOrder("IO-20260703-OFFLINE", "张三", "0400111222", paidAmount: 30m, outstandingAmount: 90m);
        var localOrder = CreateLocalInstallmentOrder(order);
        var installmentService = new CapturingInstallmentOrderService
        {
            Orders = [order],
            LocalOrders = { [order.OrderId] = localOrder }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(isOnline: false),
            installmentOrderService: installmentService);

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();
        await WaitUntilAsync(() => viewModel.SelectedReceipt is not null);

        Assert.Contains(viewModel.ReceiptLines, line => line.DisplayName == "Receipt Tea");
        Assert.True(viewModel.ReprintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Installment_history_pushes_date_terminal_and_keyword_filters_to_history_service()
    {
        var installmentService = new CapturingInstallmentOrderService();
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(deviceCode: "POS-04"),
            installmentOrderService: installmentService)
        {
            DateFrom = new DateTime(2026, 8, 25),
            DateTo = new DateTime(2026, 8, 25),
            SearchText = " ITEM-TARGET "
        };
        viewModel.IsInstallmentSourceSelected = true;
        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode == "POS-04");

        await viewModel.LoadAsync();

        Assert.NotNull(installmentService.LastHistoryQuery);
        Assert.Equal("POS-04", installmentService.LastHistoryQuery!.DeviceCode);
        Assert.Equal("ITEM-TARGET", installmentService.LastHistoryQuery.Keyword);
        Assert.Equal(new DateTime(2026, 8, 25), installmentService.LastHistoryQuery.UpdatedFrom?.Date);
        Assert.Equal(new DateTime(2026, 8, 25), installmentService.LastHistoryQuery.UpdatedTo?.Date);
        Assert.Equal(100, installmentService.LastHistoryQuery.Take);
    }

    [Fact]
    public async Task Installment_history_with_local_receipt_allows_reprint()
    {
        var order = CreateInstallmentOrder("IO-20260703-REPRINT", "王五", "0400555666", paidAmount: 40m, outstandingAmount: 80m);
        var localOrder = CreateLocalInstallmentOrder(order);
        var reprintRequested = false;
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: new CapturingInstallmentOrderService
            {
                Orders = [order],
                LocalOrders = { [order.OrderId] = localOrder }
            });
        viewModel.ReprintRequested += (_, _) => reprintRequested = true;

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();

        var receipt = Assert.IsType<ReceiptDetails>(viewModel.SelectedReceipt);
        Assert.Equal(order.OrderId, receipt.OrderGuid);
        Assert.True(viewModel.IsReprintVisible);
        Assert.True(viewModel.ReprintCommand.CanExecute(null));

        viewModel.ReprintCommand.Execute(null);

        Assert.True(reprintRequested);
    }

    [Fact]
    public async Task Installment_history_without_local_receipt_hides_reprint()
    {
        var order = CreateInstallmentOrder("IO-20260703-NO-RECEIPT", "赵六", "0400777888", paidAmount: 20m, outstandingAmount: 100m);
        var reprintRequested = false;
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: new CapturingInstallmentOrderService
            {
                Orders = [order]
            });
        viewModel.ReprintRequested += (_, _) => reprintRequested = true;

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.Null(viewModel.SelectedReceipt);
        Assert.False(viewModel.IsReprintVisible);
        Assert.False(viewModel.ReprintCommand.CanExecute(null));

        viewModel.ReprintCommand.Execute(null);

        Assert.False(reprintRequested);
    }

    [Fact]
    public async Task Installment_history_paid_off_order_hides_continue_payment()
    {
        var order = CreateInstallmentOrder("IO-20260703-0002", "李四", "0400333444", paidAmount: 120m, outstandingAmount: 0m);
        var localOrder = CreateLocalInstallmentOrder(order);
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: new CapturingInstallmentOrderService
            {
                Orders = [order],
                LocalOrders = { [order.OrderId] = localOrder }
            },
            continueInstallmentPaymentAsync: _ => Task.CompletedTask);

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Orders);
        Assert.False(row.CanContinueInstallmentPayment);
        Assert.False(viewModel.IsContinueInstallmentPaymentVisible);
        Assert.False(viewModel.ContinueInstallmentPaymentCommand.CanExecute(row));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains(order.OrderNumber, StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("TAX INVOICE", StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("Balance due", StringComparison.Ordinal) && preview.Text.Contains("$0.00", StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("Pickup: Pending", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains(nameof(InstallmentStatus.PaidOff), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Installment_history_lock_failure_keeps_rows_and_disables_pickup()
    {
        var order = CreateInstallmentOrder(
            "IO-20260825-LOCK-UNKNOWN",
            "BBB",
            "0430990026",
            paidAmount: 55m,
            outstandingAmount: 0m);
        var installmentService = new CapturingInstallmentOrderService
        {
            Orders = [order],
            LockedInstallmentsException = new InvalidOperationException("lock lookup failed")
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: installmentService);

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanConfirmInstallmentPickup);
        Assert.False(viewModel.IsConfirmInstallmentPickupVisible);
        Assert.False(viewModel.ConfirmInstallmentPickupCommand.CanExecute(row));
    }

    [Fact]
    public async Task Installment_history_paid_off_order_confirms_pickup_from_selected_row()
    {
        var order = CreateInstallmentOrder("IO-20260703-0003", "BBB", "0430990026", paidAmount: 55m, outstandingAmount: 0m);
        var installmentService = new CapturingInstallmentOrderService
        {
            Orders = [order],
            LocalOrders = { [order.OrderId] = CreateLocalInstallmentOrder(order) }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: installmentService);

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();
        await WaitUntilAsync(() => viewModel.ReceiptPreviewRows.Count > 0);

        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanConfirmInstallmentPickup);
        Assert.True(viewModel.IsConfirmInstallmentPickupVisible);
        Assert.True(viewModel.ConfirmInstallmentPickupCommand.CanExecute(row));
        Assert.Contains(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("TAX INVOICE", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.ReceiptPreviewRows, preview => preview.Text.Contains("===== INSTALLMENT =====", StringComparison.Ordinal));
        Assert.Equal(
            order.OrderId.ToString("D"),
            Assert.Single(viewModel.ReceiptPreviewRows, preview => preview.IsQrCode).QrCodeValue);

        await viewModel.ConfirmInstallmentPickupCommand.ExecuteAsync(row);

        Assert.Equal(order.OrderId, installmentService.LastConfirmPickupOrderId);
        Assert.Equal("confirmed", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Installment_history_pickup_timeout_is_handled_as_unknown_result()
    {
        var order = CreateInstallmentOrder("IO-20260703-TIMEOUT", "BBB", "0430990026", paidAmount: 55m, outstandingAmount: 0m);
        var installmentService = new CapturingInstallmentOrderService
        {
            Orders = [order],
            ConfirmPickupException = new TaskCanceledException("request timeout")
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: installmentService);

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);

        var exception = await Record.ExceptionAsync(
            () => viewModel.ConfirmInstallmentPickupCommand.ExecuteAsync(row));

        Assert.Null(exception);
        Assert.Equal(order.OrderId, installmentService.LastConfirmPickupOrderId);
        Assert.Contains("结果可能已提交", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.ConfirmInstallmentPickupCommand.CanExecute(row));
        await viewModel.ConfirmInstallmentPickupCommand.ExecuteAsync(row);
        Assert.Equal(1, installmentService.ConfirmPickupCallCount);
    }

    [Fact]
    public async Task Installment_history_restart_lock_disables_pickup_confirmation()
    {
        var order = CreateInstallmentOrder("IO-20260703-LOCKED", "BBB", "0430990026", paidAmount: 55m, outstandingAmount: 0m);
        var installmentService = new CapturingInstallmentOrderService
        {
            Orders = [order],
            LockedInstallments = new HashSet<Guid> { order.OrderId }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            installmentOrderService: installmentService);

        viewModel.IsInstallmentSourceSelected = true;
        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Orders);
        Assert.False(row.CanConfirmInstallmentPickup);
        Assert.False(viewModel.IsConfirmInstallmentPickupVisible);
        Assert.False(viewModel.ConfirmInstallmentPickupCommand.CanExecute(row));
    }

    [Fact]
    public async Task Held_source_merges_local_and_remote_rows_by_hold_guid_and_resolves_badges()
    {
        var holdGuidA = Guid.NewGuid();
        var holdGuidB = Guid.NewGuid();
        var holdGuidC = Guid.NewGuid();
        var holdGuidD = Guid.NewGuid();
        var holdGuidE = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                HeldSummary(holdGuidA, "POS-01", 10m, 1m, 9m, 2),
                HeldSummary(holdGuidB, "POS-01", 20m, 0m, 20m, 1),
                HeldSummary(holdGuidC, "POS-01", 30m, 0m, 30m, 1),
                HeldSummary(holdGuidD, "POS-01", 40m, 0m, 40m, 1)
            ]
        };
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuidA] = HeldPublication(holdGuidA, SharedHeldOrderPublicationStatus.Published),
                [holdGuidB] = HeldPublication(holdGuidB, SharedHeldOrderPublicationStatus.PendingPublish),
                [holdGuidC] = HeldPublication(
                    holdGuidC,
                    SharedHeldOrderPublicationStatus.Blocked,
                    "ReturnLineNotSupported",
                    "return line")
            }
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>(
            [
                HeldItem(holdGuidA, totalCents: 1100, discountCents: 200, actualCents: 900, lineCount: 3),
                HeldItem(holdGuidE, totalCents: 5000, discountCents: 0, actualCents: 5000, lineCount: 2)
            ])
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderCoordinator: new CapturingSharedHeldOrderCoordinator(),
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: repository);

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.Equal(5, viewModel.Orders.Count);
        Assert.Equal(5, viewModel.Orders.Select(order => order.OrderGuid).Distinct().Count());

        var merged = viewModel.Orders.Single(order => order.OrderGuid == holdGuidA);
        Assert.True(merged.IsHeldOrder);
        Assert.Equal(11m, merged.TotalAmount);
        Assert.Equal(2m, merged.DiscountAmount);
        Assert.Equal(9m, merged.ActualAmount);
        Assert.Equal(3, merged.LineCount);
        Assert.Equal(HeldOrderBadgeKind.RemotePending, merged.HeldBadgeKind);
        Assert.True(merged.CanRemoteRecall);
        Assert.True(merged.CanOfflineRecall);
        Assert.Equal("Remote recall", merged.StatusLabel);
        Assert.Equal("Suspended", merged.PaymentSummary);

        var pendingPublish = viewModel.Orders.Single(order => order.OrderGuid == holdGuidB);
        Assert.Equal(HeldOrderBadgeKind.LocalPendingPublish, pendingPublish.HeldBadgeKind);
        Assert.Equal("Pending publish", pendingPublish.StatusLabel);
        Assert.True(pendingPublish.CanOfflineRecall);
        Assert.False(pendingPublish.CanLegacyRecall);
        Assert.False(pendingPublish.CanRemoteRecall);

        var blocked = viewModel.Orders.Single(order => order.OrderGuid == holdGuidC);
        Assert.Equal(HeldOrderBadgeKind.Blocked, blocked.HeldBadgeKind);
        Assert.Equal("Blocked: ReturnLineNotSupported return line", blocked.StatusLabel);
        Assert.Equal("ReturnLineNotSupported return line", blocked.HeldStatusDetail);
        Assert.True(blocked.CanLegacyRecall);
        Assert.False(blocked.CanOfflineRecall);

        var legacy = viewModel.Orders.Single(order => order.OrderGuid == holdGuidD);
        Assert.Equal(HeldOrderBadgeKind.LocalHold, legacy.HeldBadgeKind);
        Assert.Equal("Local hold", legacy.StatusLabel);
        Assert.True(legacy.CanLegacyRecall);
        Assert.False(legacy.CanOfflineRecall);

        var remoteOnly = viewModel.Orders.Single(order => order.OrderGuid == holdGuidE);
        Assert.Equal(HeldOrderBadgeKind.RemotePending, remoteOnly.HeldBadgeKind);
        Assert.True(remoteOnly.CanRemoteRecall);
        Assert.False(remoteOnly.CanOfflineRecall);
        Assert.False(remoteOnly.IsSuspendedOrder);
    }

    [Fact]
    public async Task Held_local_scope_remote_error_keeps_local_rows_without_showing_remote_status()
    {
        var holdGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
        };
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuid] = HeldPublication(holdGuid, SharedHeldOrderPublicationStatus.PendingPublish)
            }
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => throw new SharedHeldOrderApiException(
                SharedHeldOrderApiErrorKind.Retryable,
                "remote unavailable",
                null,
                HttpStatusCode.BadGateway)
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: repository);

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Orders);
        Assert.Equal(holdGuid, row.OrderGuid);
        Assert.Equal(HeldOrderBadgeKind.LocalPendingPublish, row.HeldBadgeKind);
        Assert.Equal(string.Empty, viewModel.HeldOrdersRemoteStatusMessage);
        Assert.False(viewModel.IsHeldRemoteErrorVisible);
    }

    [Fact]
    public async Task Held_auto_refresh_runs_every_10_seconds_while_visible_and_stops_on_hidden_dispose_and_source_change()
    {
        var holdGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
        };
        var clock = new ManualTimeProvider();
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderCoordinator: new CapturingSharedHeldOrderCoordinator(),
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository(),
            timeProvider: clock);

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        Assert.Empty(clock.Timers);

        viewModel.OnScreenShown();
        await viewModel.LastHeldAutoRefreshTask!;
        var timer = Assert.Single(clock.Timers);
        Assert.Equal(TimeSpan.FromSeconds(10), timer.CreatedDueTime);
        Assert.Equal(TimeSpan.FromSeconds(10), timer.CreatedPeriod);

        suspendedOrders.PendingOrders =
        [
            HeldSummary(Guid.NewGuid(), "POS-01", 99m, 0m, 99m, 1)
        ];
        timer.Fire();
        await viewModel.LastHeldAutoRefreshTask!;

        var refreshed = Assert.Single(viewModel.Orders);
        Assert.Equal(99m, refreshed.ActualAmount);

        viewModel.OnScreenHidden();
        Assert.True(timer.IsDisposed);
        Assert.All(clock.Timers, created => Assert.True(created.IsDisposed));

        viewModel.IsLocalSourceSelected = true;
        await viewModel.LoadAsync();
        viewModel.OnScreenShown();
        Assert.All(clock.Timers, created => Assert.True(created.IsDisposed));

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        viewModel.OnScreenShown();
        var secondTimer = clock.Timers.Single(created => !created.IsDisposed);
        Assert.False(secondTimer.IsDisposed);

        viewModel.Dispose();
        Assert.True(secondTimer.IsDisposed);
        var countBefore = viewModel.Orders.Count;
        secondTimer.Fire();
        await (viewModel.LastHeldAutoRefreshTask ?? Task.CompletedTask);
        Assert.Equal(countBefore, viewModel.Orders.Count);
    }

    [Fact]
    public async Task Held_refresh_reuses_in_flight_remote_request_until_it_completes()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<SharedHeldOrderListItemDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCalls = 0;
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = cancellationToken =>
            {
                remoteCalls += 1;
                cancellationToken.Register(() => cancellationObserved.TrySetResult());
                return gate.Task;
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository());

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        await viewModel.LoadAsync();
        Assert.Equal(1, remoteCalls);
        Assert.False(cancellationObserved.Task.IsCompleted);
        gate.SetResult([]);
        await (viewModel.LastHeldRemoteRefreshTask ?? Task.CompletedTask);

        Assert.False(cancellationObserved.Task.IsCompleted);
    }

    [Fact]
    public async Task Held_remote_recall_uses_coordinator_and_returns_to_pos()
    {
        var holdGuid = Guid.NewGuid();
        var coordinator = new CapturingSharedHeldOrderCoordinator();
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>(
            [
                HeldItem(holdGuid, totalCents: 1000, discountCents: 0, actualCents: 1000, lineCount: 1)
            ])
        };
        var returnedToPos = false;
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            () =>
            {
                returnedToPos = true;
                return Task.CompletedTask;
            },
            sharedHeldOrderCoordinator: coordinator,
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository());

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanRecall);

        await viewModel.RecallOrderCommand.ExecuteAsync(row);

        var take = Assert.Single(coordinator.RemoteTakes);
        Assert.Equal(holdGuid, take.HoldGuid);
        Assert.Empty(coordinator.LocalRecalls);
        Assert.True(returnedToPos);
    }

    [Fact]
    public async Task Held_remote_recall_timeout_is_handled_without_escaping_command()
    {
        var coordinator = new CapturingSharedHeldOrderCoordinator
        {
            TakeRemoteHandler = (_, _) => Task.FromException<SharedHeldOrderTakeResult>(
                new TaskCanceledException("held recall timed out"))
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderCoordinator: coordinator);
        var row = HeldHistoryRow(Guid.NewGuid(), canRecall: true, canRemoteRecall: true);

        var exception = await Record.ExceptionAsync(() => viewModel.RecallOrderCommand.ExecuteAsync(row));

        Assert.Null(exception);
        Assert.Single(coordinator.RemoteTakes);
        Assert.Equal("held recall timed out", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Held_offline_recall_uses_local_publication_without_touching_server()
    {
        var holdGuid = Guid.NewGuid();
        var coordinator = new CapturingSharedHeldOrderCoordinator();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuid] = HeldPublication(holdGuid, SharedHeldOrderPublicationStatus.Published)
            }
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession() with { IsOnline = false },
            sharedHeldOrderCoordinator: coordinator,
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: repository);

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanOfflineRecall);
        Assert.False(row.CanRemoteRecall);

        await viewModel.RecallOrderCommand.ExecuteAsync(row);

        var recall = Assert.Single(coordinator.LocalRecalls);
        Assert.Equal(holdGuid, recall.HoldGuid);
        Assert.Empty(coordinator.RemoteTakes);
    }

    [Fact]
    public async Task Held_recall_with_publication_does_not_fall_back_to_legacy_suspended_recall()
    {
        var holdGuid = Guid.NewGuid();
        var coordinator = new CapturingSharedHeldOrderCoordinator();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
        };
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuid] = HeldPublication(holdGuid, SharedHeldOrderPublicationStatus.PendingPublish)
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderCoordinator: coordinator,
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
            },
            sharedHeldOrderRepository: repository);

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);

        await viewModel.RecallOrderCommand.ExecuteAsync(row);

        Assert.Single(coordinator.LocalRecalls);
        Assert.Null(suspendedOrders.RecalledOrderGuid);
    }

    [Fact]
    public async Task Held_delete_is_only_available_for_unclaimed_local_order_from_this_device()
    {
        var localHoldGuid = Guid.NewGuid();
        var otherDeviceHoldGuid = Guid.NewGuid();
        var remoteOnlyHoldGuid = Guid.NewGuid();
        var claimedHoldGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Claims =
            [
                HeldRecoveryClaim(claimedHoldGuid, Guid.NewGuid(), LocalClaimStatus.Prepared)
            ],
            Publications =
            {
                [localHoldGuid] = HeldPublication(localHoldGuid, SharedHeldOrderPublicationStatus.Published),
                [otherDeviceHoldGuid] = HeldPublication(otherDeviceHoldGuid, SharedHeldOrderPublicationStatus.NeedsEvaluation),
                [claimedHoldGuid] = HeldPublication(claimedHoldGuid, SharedHeldOrderPublicationStatus.Published)
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders =
                [
                    HeldSummary(localHoldGuid, "POS-01", 10m, 0m, 10m, 1),
                    HeldSummary(otherDeviceHoldGuid, "POS-02", 20m, 0m, 20m, 1),
                    HeldSummary(claimedHoldGuid, "POS-01", 40m, 0m, 40m, 1)
                ]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>
                ([
                    HeldItem(localHoldGuid, 1000, 0, 1000, 1),
                    HeldItem(remoteOnlyHoldGuid, 3000, 0, 3000, 1)
                ])
            },
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.True(viewModel.Orders.Single(order => order.OrderGuid == localHoldGuid).CanDeleteHeldOrder);
        Assert.False(viewModel.Orders.Single(order => order.OrderGuid == remoteOnlyHoldGuid).CanDeleteHeldOrder);
        Assert.DoesNotContain(viewModel.Orders, order => order.OrderGuid == claimedHoldGuid);
        // 非本机挂单及 synthetic RemoteClaim 不在本机页签出现；切页后仍不可删除。
        Assert.DoesNotContain(viewModel.Orders, order => order.OrderGuid == otherDeviceHoldGuid);
        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        Assert.False(viewModel.Orders.Single(order => order.OrderGuid == otherDeviceHoldGuid).CanDeleteHeldOrder);
        Assert.False(viewModel.Orders.Single(order => order.OrderGuid == claimedHoldGuid).CanDeleteHeldOrder);
    }

    [Fact]
    public async Task Held_delete_pending_row_can_retry_delete_but_cannot_be_recalled()
    {
        var holdGuid = Guid.NewGuid();
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>
                ([HeldItem(holdGuid, 1000, 0, 1000, 1)])
            },
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository
            {
                Publications =
                {
                    [holdGuid] = HeldPublication(
                        holdGuid,
                        SharedHeldOrderPublicationStatus.Blocked,
                        "LOCAL_DELETE_PENDING_REMOTE")
                }
            });

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanDeleteHeldOrder);
        Assert.False(row.CanRecall);
        Assert.DoesNotContain("LOCAL_DELETE_PENDING", row.StatusLabel, StringComparison.Ordinal);
        Assert.Contains("retry", row.StatusLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Held_delete_confirmed_cancels_remote_then_completes_local_and_refreshes()
    {
        var holdGuid = Guid.NewGuid();
        var remoteCancelled = false;
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
        };
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuid] = HeldPublication(holdGuid, SharedHeldOrderPublicationStatus.Published)
            },
            StageDelete = (actualHoldGuid, _, _, _) => new SharedHeldOrderDeleteStage(actualHoldGuid, true),
            CompleteDelete = (_, _, _, _) =>
            {
                suspendedOrders.PendingOrders = [];
                return true;
            }
        };
        var confirmation = new CapturingConfirmationDialogService
        {
            HeldOrderCancellationResult = true
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>(
                remoteCancelled ? [] : [HeldItem(holdGuid, 1000, 0, 1000, 1)]),
            Cancel = (actualHoldGuid, _) =>
            {
                Assert.Equal(holdGuid, actualHoldGuid);
                remoteCancelled = true;
                return Task.FromResult(new SharedHeldOrderCancelResponse(
                    actualHoldGuid,
                    HeldServerStatus.Cancelled,
                    2,
                    new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero)));
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            confirmationDialogService: confirmation,
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);

        await viewModel.DeleteHeldOrderCommand.ExecuteAsync(row);

        Assert.Equal(1, confirmation.HeldOrderCancellationCallCount);
        Assert.Single(repository.DeleteStages);
        Assert.Single(repository.DeleteCompletions);
        Assert.Empty(viewModel.Orders);
        Assert.Equal("Held sale deleted.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Held_delete_not_found_publishes_frozen_payload_then_cancels_and_completes()
    {
        var holdGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
        };
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuid] = HeldPublication(
                    holdGuid,
                    SharedHeldOrderPublicationStatus.PendingPublish,
                    shareRequestedAtIso: "2026-08-11T01:00:00.000Z")
            },
            StageDelete = (actualHoldGuid, _, _, _) =>
                new SharedHeldOrderDeleteStage(actualHoldGuid, true),
            CompleteDelete = (_, _, _, _) =>
            {
                suspendedOrders.PendingOrders = [];
                return true;
            },
            PublicationPayload = _ => SampleCanonical()
        };
        var cancelCalls = 0;
        var publishCalls = 0;
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([]),
            Cancel = (actualHoldGuid, _) =>
            {
                cancelCalls += 1;
                if (cancelCalls == 1)
                {
                    throw new SharedHeldOrderApiException(
                        SharedHeldOrderApiErrorKind.Invalid,
                        "not found",
                        "SHARED_HELD_ORDER_NOT_FOUND",
                        HttpStatusCode.NotFound);
                }

                return Task.FromResult(new SharedHeldOrderCancelResponse(
                    actualHoldGuid,
                    HeldServerStatus.Cancelled,
                    2,
                    new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero)));
            },
            Publish = (request, _) =>
            {
                publishCalls += 1;
                Assert.Equal(holdGuid, request.HoldGuid);
                Assert.Equal("S001", request.StoreCode);
                Assert.Equal("POS-01", request.DeviceCode);
                Assert.Equal(holdGuid.ToString("D"), request.IdempotencyKey);
                return Task.FromResult(new SharedHeldOrderPublishResponse(
                    holdGuid,
                    HeldServerStatus.Pending,
                    1,
                    new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero)));
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            confirmationDialogService: new CapturingConfirmationDialogService
            {
                HeldOrderCancellationResult = true
            },
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        await viewModel.DeleteHeldOrderCommand.ExecuteAsync(Assert.Single(viewModel.Orders));

        Assert.Equal(1, publishCalls);
        Assert.Equal(2, cancelCalls);
        Assert.Single(repository.DeleteCompletions);
        Assert.Empty(viewModel.Orders);
    }

    [Fact]
    public async Task Held_delete_remote_failure_keeps_staged_local_order_for_retry()
    {
        var holdGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuid] = HeldPublication(holdGuid, SharedHeldOrderPublicationStatus.Published)
            },
            CompleteDelete = (_, _, _, _) => throw new InvalidOperationException("must not complete")
        };
        repository.StageDelete = (actualHoldGuid, _, _, _) =>
        {
            // 真实 repository 会在远端取消前先落 Blocked 删除意图；失败后页面必须立即重读。
            repository.Publications[actualHoldGuid] = HeldPublication(
                actualHoldGuid,
                SharedHeldOrderPublicationStatus.Blocked,
                "LOCAL_DELETE_PENDING_REMOTE");
            return new SharedHeldOrderDeleteStage(actualHoldGuid, true);
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            confirmationDialogService: new CapturingConfirmationDialogService
            {
                HeldOrderCancellationResult = true
            },
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>
                ([HeldItem(holdGuid, 1000, 0, 1000, 1)]),
                Cancel = (_, _) => throw new InvalidOperationException("cancel failed")
            },
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        await viewModel.DeleteHeldOrderCommand.ExecuteAsync(Assert.Single(viewModel.Orders));

        Assert.Single(repository.DeleteStages);
        Assert.Empty(repository.DeleteCompletions);
        var retryRow = Assert.Single(viewModel.Orders);
        Assert.False(retryRow.CanRecall);
        Assert.True(retryRow.CanDeleteHeldOrder);
        Assert.Equal("Deletion is pending; choose Delete to retry.", retryRow.HeldStatusDetail);
        Assert.Equal("cancel failed", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Held_delete_timeout_keeps_staged_state_without_escaping_command()
    {
        var holdGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            StageDelete = (actualHoldGuid, _, _, _) => new SharedHeldOrderDeleteStage(actualHoldGuid, true),
            CompleteDelete = (_, _, _, _) => throw new InvalidOperationException("must not complete")
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            confirmationDialogService: new CapturingConfirmationDialogService
            {
                HeldOrderCancellationResult = true
            },
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                Cancel = (_, _) => Task.FromException<SharedHeldOrderCancelResponse>(
                    new TaskCanceledException("held delete timed out"))
            },
            sharedHeldOrderRepository: repository);
        var row = HeldHistoryRow(holdGuid, canDeleteHeldOrder: true);

        var exception = await Record.ExceptionAsync(() => viewModel.DeleteHeldOrderCommand.ExecuteAsync(row));

        Assert.Null(exception);
        Assert.Single(repository.DeleteStages);
        Assert.Empty(repository.DeleteCompletions);
        Assert.Equal("held delete timed out", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Held_delete_cancelled_confirmation_does_not_stage_or_call_server()
    {
        var holdGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuid] = HeldPublication(holdGuid, SharedHeldOrderPublicationStatus.NeedsEvaluation)
            }
        };
        var confirmation = new CapturingConfirmationDialogService
        {
            HeldOrderCancellationResult = false
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            confirmationDialogService: confirmation,
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([]),
                Cancel = (_, _) => throw new InvalidOperationException("must not cancel")
            },
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        await viewModel.DeleteHeldOrderCommand.ExecuteAsync(Assert.Single(viewModel.Orders));

        Assert.Equal(1, confirmation.HeldOrderCancellationCallCount);
        Assert.Empty(repository.DeleteStages);
        Assert.Empty(repository.DeleteCompletions);
    }

    [Fact]
    public async Task Force_release_requires_non_empty_reason_and_calls_api_with_trimmed_reason()
    {
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Claims =
            [
                HeldRecoveryClaim(holdGuid, claimGuid, LocalClaimStatus.Prepared)
            ]
        };
        (Guid HoldGuid, Guid ClaimGuid, string Reason, PosSessionState Session)? captured = null;
        var coordinator = new CapturingSharedHeldOrderCoordinator
        {
            ForceReleaseHandler = (actualHoldGuid, actualClaimGuid, reason, session) =>
            {
                captured = (actualHoldGuid, actualClaimGuid, reason, session);
                return Task.CompletedTask;
            }
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderCoordinator: coordinator,
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanForceRelease);
        Assert.Equal(claimGuid, row.HeldClaimId);
        Assert.Equal(HeldOrderBadgeKind.LocalClaimPrepared, row.HeldBadgeKind);

        viewModel.ForceReleaseHeldOrderCommand.Execute(row);
        Assert.True(viewModel.IsForceReleaseReasonPromptOpen);
        Assert.False(viewModel.ConfirmForceReleaseCommand.CanExecute(null));

        viewModel.ForceReleaseReason = "  supervisor override  ";
        Assert.True(viewModel.ConfirmForceReleaseCommand.CanExecute(null));

        await viewModel.ConfirmForceReleaseCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(holdGuid, captured!.Value.HoldGuid);
        Assert.Equal(claimGuid, captured.Value.ClaimGuid);
        Assert.Equal("supervisor override", captured.Value.Reason);
        Assert.False(viewModel.IsForceReleaseReasonPromptOpen);
        Assert.Contains("force-released", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Force_release_failure_keeps_prompt_closed_and_shows_retryable_status()
    {
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Claims =
            [
                HeldRecoveryClaim(holdGuid, claimGuid, LocalClaimStatus.Active)
            ]
        };
        var coordinator = new CapturingSharedHeldOrderCoordinator
        {
            ForceReleaseHandler = (_, _, _, _) => throw new InvalidOperationException("force release failed")
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderCoordinator: coordinator,
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);

        viewModel.ForceReleaseHeldOrderCommand.Execute(row);
        viewModel.ForceReleaseReason = "supervisor override";
        await viewModel.ConfirmForceReleaseCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsForceReleaseReasonPromptOpen);
        Assert.Equal("force release failed", viewModel.StatusMessage);
        Assert.Single(coordinator.ForceReleases);
    }

    [Fact]
    public async Task Force_release_timeout_is_handled_without_escaping_command()
    {
        var claimGuid = Guid.NewGuid();
        var coordinator = new CapturingSharedHeldOrderCoordinator
        {
            ForceReleaseHandler = (_, _, _, _) => Task.FromException(
                new TaskCanceledException("force release timed out"))
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderCoordinator: coordinator);
        var row = HeldHistoryRow(Guid.NewGuid(), heldClaimId: claimGuid, canForceRelease: true);

        viewModel.ForceReleaseHeldOrderCommand.Execute(row);
        viewModel.ForceReleaseReason = "银行超时核对";
        var exception = await Record.ExceptionAsync(() => viewModel.ConfirmForceReleaseCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Single(coordinator.ForceReleases);
        Assert.Equal("force release timed out", viewModel.StatusMessage);
        Assert.False(viewModel.IsForceReleaseReasonPromptOpen);
    }

    [Fact]
    public async Task Held_first_load_failure_does_not_fall_back_to_local_or_online_rows()
    {
        var localOrder = new LocalOrderSummary(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            "Synced",
            1,
            "Cash");
        var suspendedOrders = new CapturingSuspendedOrderService();
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService { Orders = [localOrder] },
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession());

        // 先在 Local 源加载出旧列表。
        viewModel.DateFrom = localOrder.SoldAt.Date;
        viewModel.DateTo = localOrder.SoldAt.Date;
        await viewModel.LoadAsync();
        Assert.Single(viewModel.Orders);

        // 切到 Held 且首次加载失败：不得回退显示 Local/Online 旧列表。
        suspendedOrders.ThrowOnGetPendingOrders = true;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Orders);
        Assert.NotEmpty(viewModel.StatusMessage);
        Assert.Empty(viewModel.HeldOrdersRemoteStatusMessage);
    }

    [Fact]
    public async Task Held_orders_default_to_local_scope_and_split_local_other_by_device()
    {
        var localHold = Guid.NewGuid();
        var otherHold = Guid.NewGuid();
        var localRemoteOrphan = Guid.NewGuid();
        var otherRemoteOnly = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                HeldSummary(localHold, "POS-01", 10m, 0m, 10m, 1),
                HeldSummary(otherHold, "POS-02", 20m, 0m, 20m, 1)
            ]
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>(
            [
                HeldItem(localRemoteOrphan, totalCents: 1100, discountCents: 0, actualCents: 1100, lineCount: 1),
                HeldItem(otherRemoteOnly, totalCents: 2200, discountCents: 0, actualCents: 2200, lineCount: 1, deviceCode: "POS-02")
            ])
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository());

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        // 默认本机页签：本机挂单 + 本机远端孤儿；非本机一律不显示。
        Assert.True(viewModel.IsHeldLocalScopeSelected);
        Assert.False(viewModel.IsHeldOtherScopeSelected);
        Assert.Equal(2, viewModel.Orders.Count);
        Assert.Contains(viewModel.Orders, order => order.OrderGuid == localHold);
        Assert.Contains(viewModel.Orders, order => order.OrderGuid == localRemoteOrphan);
        Assert.DoesNotContain(viewModel.Orders, order => order.OrderGuid == otherHold);
        Assert.DoesNotContain(viewModel.Orders, order => order.OrderGuid == otherRemoteOnly);

        // 非本机页签：其他 device 挂单/远端孤儿。
        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        Assert.Equal(2, viewModel.Orders.Count);
        Assert.Contains(viewModel.Orders, order => order.OrderGuid == otherHold);
        Assert.Contains(viewModel.Orders, order => order.OrderGuid == otherRemoteOnly);
        Assert.DoesNotContain(viewModel.Orders, order => order.OrderGuid == localHold);
        Assert.DoesNotContain(viewModel.Orders, order => order.OrderGuid == localRemoteOrphan);
    }

    [Fact]
    public async Task Held_orders_reset_to_local_scope_every_time_source_is_entered()
    {
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
            },
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository());

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        Assert.True(viewModel.IsHeldOtherScopeSelected);

        viewModel.IsLocalSourceSelected = true;
        viewModel.IsHeldSourceSelected = true;

        Assert.True(viewModel.IsHeldLocalScopeSelected);
        Assert.False(viewModel.IsHeldOtherScopeSelected);
    }

    [Fact]
    public async Task Held_synthetic_remote_claim_belongs_to_other_scope_even_on_current_device()
    {
        var holdGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Claims = [HeldRecoveryClaim(holdGuid, Guid.NewGuid(), LocalClaimStatus.Prepared)]
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                // 服务端状态收敛存在短暂窗口时，仍可能返回同一 HoldGuid；
                // synthetic 来源必须优先，不能把它作为“本机远端孤儿”重复显示。
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>(
                    [HeldItem(holdGuid, 1000, 0, 1000, 1, deviceCode: "POS-01")])
            },
            sharedHeldOrderRepository: repository);

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        await viewModel.LastHeldRemoteRefreshTask!;
        Assert.DoesNotContain(viewModel.Orders, order => order.OrderGuid == holdGuid);

        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        await viewModel.LastHeldRemoteRefreshTask!;
        Assert.Equal(holdGuid, Assert.Single(viewModel.Orders).OrderGuid);
    }

    [Fact]
    public async Task Held_other_scope_remote_failure_retains_cached_rows_and_exposes_error_only_there()
    {
        var remoteHoldGuid = Guid.NewGuid();
        var failRemote = false;
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => failRemote
                ? Task.FromException<IReadOnlyList<SharedHeldOrderListItemDto>>(
                    new InvalidOperationException("remote unavailable"))
                : Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>(
                    [HeldItem(remoteHoldGuid, 1000, 0, 1000, 1, deviceCode: "POS-02")])
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository());

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        Assert.Equal(remoteHoldGuid, Assert.Single(viewModel.Orders).OrderGuid);

        failRemote = true;
        await viewModel.LoadAsync();

        Assert.Equal(remoteHoldGuid, Assert.Single(viewModel.Orders).OrderGuid);
        Assert.True(viewModel.IsHeldRemoteErrorVisible);
        viewModel.IsHeldLocalScopeSelected = true;
        Assert.False(viewModel.IsHeldRemoteErrorVisible);
    }

    [Fact]
    public async Task Held_published_local_row_is_retained_on_remote_failure_then_removed_after_authoritative_success()
    {
        var holdGuid = Guid.NewGuid();
        var failRemote = true;
        var repository = new CapturingSharedHeldOrderRepository
        {
            Publications =
            {
                [holdGuid] = HeldPublication(
                    holdGuid,
                    SharedHeldOrderPublicationStatus.Published,
                    shareRequestedAtIso: "2026-07-01T09:00:00.000Z")
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => failRemote
                    ? Task.FromException<IReadOnlyList<SharedHeldOrderListItemDto>>(
                        new InvalidOperationException("offline"))
                    : Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
            },
            sharedHeldOrderRepository: repository);

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        Assert.Equal(holdGuid, Assert.Single(viewModel.Orders).OrderGuid);

        failRemote = false;
        await viewModel.LoadAsync();
        Assert.Empty(viewModel.Orders);
    }

    [Fact]
    public async Task Held_local_scope_shows_local_rows_before_remote_completes()
    {
        var holdGuid = Guid.NewGuid();
        var gate = new TaskCompletionSource<IReadOnlyList<SharedHeldOrderListItemDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => gate.Task
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository());

        viewModel.IsHeldSourceSelected = true;
        var loadTask = viewModel.LoadAsync();

        // 本地先显示：远端未返回时本机行已出现在 Orders。
        await WaitUntilAsync(() => viewModel.Orders.Count == 1);
        Assert.Equal(holdGuid, Assert.Single(viewModel.Orders).OrderGuid);
        await loadTask;
        Assert.True(loadTask.IsCompletedSuccessfully);
        Assert.NotNull(viewModel.LastHeldRemoteRefreshTask);
        Assert.False(viewModel.LastHeldRemoteRefreshTask!.IsCompleted);

        gate.SetResult([HeldItem(holdGuid, 1000, 0, 1000, 1)]);
        await viewModel.LastHeldRemoteRefreshTask;

        Assert.Equal(holdGuid, Assert.Single(viewModel.Orders).OrderGuid);
    }

    [Fact]
    public async Task Held_share_command_persists_request_and_runs_worker_once()
    {
        var holdGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository();
        repository.Publications[holdGuid] = HeldPublication(
            holdGuid,
            SharedHeldOrderPublicationStatus.NeedsEvaluation);
        repository.RequestShare = (actualHoldGuid, storeCode, deviceCode, requestedAt) =>
        {
            repository.Publications[actualHoldGuid] = HeldPublication(
                actualHoldGuid,
                SharedHeldOrderPublicationStatus.NeedsEvaluation,
                shareRequestedAtIso: requestedAt);
            return SharedHeldOrderShareRequestResult.Requested;
        };
        var worker = new CapturingSharedHeldOrderPublicationWorker();
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
            },
            sharedHeldOrderRepository: repository,
            sharedHeldOrderPublicationWorker: worker);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanShare);
        Assert.Equal(string.Empty, row.ShareStatusLabel);

        await viewModel.ShareHeldOrderCommand.ExecuteAsync(row);
        if (viewModel.LastSharePublicationTask is not null)
        {
            await viewModel.LastSharePublicationTask;
        }

        var request = Assert.Single(repository.ShareRequests);
        Assert.Equal(holdGuid, request.HoldGuid);
        Assert.Equal("S001", request.StoreCode);
        Assert.Equal("POS-01", request.DeviceCode);
        var run = Assert.Single(worker.Runs);
        Assert.Equal("S001", run.StoreCode);
        Assert.Equal("POS-01", run.DeviceCode);
        var refreshed = Assert.Single(viewModel.Orders);
        Assert.False(refreshed.CanShare);
        Assert.Equal("Awaiting share", refreshed.ShareStatusLabel);
    }

    [Fact]
    public async Task Held_share_request_timeout_is_handled_without_escaping_command()
    {
        var holdGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            RequestShare = (_, _, _, _) => throw new TaskCanceledException("share request timed out")
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderRepository: repository);
        var row = HeldHistoryRow(holdGuid, canShare: true);

        var exception = await Record.ExceptionAsync(() => viewModel.ShareHeldOrderCommand.ExecuteAsync(row));

        Assert.Null(exception);
        Assert.False(row.Share.IsBusy);
        Assert.Equal("share request timed out", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Held_share_worker_timeout_is_observed_and_reported()
    {
        var holdGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            RequestShare = (_, _, _, _) => SharedHeldOrderShareRequestResult.Requested
        };
        var worker = new CapturingSharedHeldOrderPublicationWorker
        {
            RunException = new TaskCanceledException("share worker timed out")
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderRepository: repository,
            sharedHeldOrderPublicationWorker: worker);
        var row = HeldHistoryRow(holdGuid, canShare: true);
        viewModel.Orders.Add(row);

        await viewModel.ShareHeldOrderCommand.ExecuteAsync(row);
        var publicationTask = Assert.IsAssignableFrom<Task>(viewModel.LastSharePublicationTask);
        var exception = await Record.ExceptionAsync(() => publicationTask);

        Assert.Null(exception);
        Assert.Equal("share worker timed out", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Held_share_requires_local_pending_unrequested_row()
    {
        var canShare = Guid.NewGuid();
        var requested = Guid.NewGuid();
        var claimed = Guid.NewGuid();
        var deletePending = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Claims =
            [
                HeldRecoveryClaim(claimed, Guid.NewGuid(), LocalClaimStatus.Prepared)
            ],
            Publications =
            {
                [requested] = HeldPublication(
                    requested,
                    SharedHeldOrderPublicationStatus.NeedsEvaluation,
                    shareRequestedAtIso: "2026-07-01T09:00:00.000Z"),
                [claimed] = HeldPublication(claimed, SharedHeldOrderPublicationStatus.PendingPublish),
                [deletePending] = HeldPublication(
                    deletePending,
                    SharedHeldOrderPublicationStatus.Blocked,
                    "LOCAL_DELETE_PENDING_LOCAL"),
                [blocked] = HeldPublication(
                    blocked,
                    SharedHeldOrderPublicationStatus.Blocked,
                    "ReturnLineNotSupported")
            }
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders =
                [
                    HeldSummary(canShare, "POS-01", 10m, 0m, 10m, 1),
                    HeldSummary(requested, "POS-01", 11m, 0m, 11m, 1),
                    HeldSummary(claimed, "POS-01", 12m, 0m, 12m, 1),
                    HeldSummary(deletePending, "POS-01", 13m, 0m, 13m, 1),
                    HeldSummary(blocked, "POS-01", 14m, 0m, 14m, 1)
                ]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
            },
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.True(viewModel.Orders.Single(order => order.OrderGuid == canShare).CanShare);
        Assert.False(viewModel.Orders.Single(order => order.OrderGuid == requested).CanShare);
        Assert.DoesNotContain(viewModel.Orders, order => order.OrderGuid == claimed);
        Assert.False(viewModel.Orders.Single(order => order.OrderGuid == deletePending).CanShare);
        Assert.False(viewModel.Orders.Single(order => order.OrderGuid == blocked).CanShare);
        Assert.Equal(string.Empty, viewModel.Orders.Single(order => order.OrderGuid == canShare).ShareStatusLabel);
        Assert.Equal("Awaiting share", viewModel.Orders.Single(order => order.OrderGuid == requested).ShareStatusLabel);
        Assert.Equal("Cannot share", viewModel.Orders.Single(order => order.OrderGuid == blocked).ShareStatusLabel);
        Assert.Equal("Cannot share", viewModel.Orders.Single(order => order.OrderGuid == deletePending).ShareStatusLabel);

        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        var claimedRow = Assert.Single(viewModel.Orders, order => order.OrderGuid == claimed);
        Assert.False(claimedRow.CanShare);
        Assert.Equal("Cannot share", claimedRow.ShareStatusLabel);
    }

    [Fact]
    public async Task Held_blocked_share_unavailable_but_local_recall_still_works()
    {
        var holdGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
            },
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository
            {
                Publications =
                {
                    [holdGuid] = HeldPublication(
                        holdGuid,
                        SharedHeldOrderPublicationStatus.Blocked,
                        "ReturnLineNotSupported",
                        "return line")
                }
            });

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);

        // 共享 Blocked 不阻止本机 recall，但不可共享。
        Assert.False(row.CanShare);
        Assert.Equal("Cannot share", row.ShareStatusLabel);
        Assert.True(row.CanLegacyRecall);
        Assert.True(row.CanRecall);
        Assert.False(viewModel.ShareHeldOrderCommand.CanExecute(row));

        await viewModel.RecallOrderCommand.ExecuteAsync(row);
        Assert.Equal(holdGuid, suspendedOrders.RecalledOrderGuid);
    }

    [Fact]
    public async Task Held_orders_hide_terminal_filter_and_keep_date_and_search()
    {
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient
            {
                ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([])
            },
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository());

        Assert.True(viewModel.IsTerminalFilterVisible);
        Assert.False(viewModel.IsHeldScopeSelectorVisible);

        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.False(viewModel.IsTerminalFilterVisible);
        Assert.True(viewModel.IsHeldScopeSelectorVisible);

        viewModel.IsLocalSourceSelected = true;
        Assert.True(viewModel.IsTerminalFilterVisible);
        Assert.False(viewModel.IsHeldScopeSelectorVisible);
    }

    [Fact]
    public async Task Force_release_is_denied_without_recall_permission()
    {
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var repository = new CapturingSharedHeldOrderRepository
        {
            Claims =
            [
                HeldRecoveryClaim(holdGuid, claimGuid, LocalClaimStatus.Active)
            ]
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([]),
            ForceRelease = (_, _, _, _) => throw new InvalidOperationException("must not be called")
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService
            {
                PendingOrders = [HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)]
            },
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            enforcePermissionsWhenNoCashier: true,
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: repository);

        viewModel.DateFrom = HeldFixtureDate;
        viewModel.DateTo = HeldFixtureDate;
        viewModel.IsHeldSourceSelected = true;
        viewModel.IsHeldOtherScopeSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);
        Assert.Equal(HeldOrderBadgeKind.LocalClaimActive, row.HeldBadgeKind);
        Assert.True(row.CanForceRelease);

        viewModel.ForceReleaseHeldOrderCommand.Execute(row);
        viewModel.ForceReleaseReason = "supervisor override";
        await viewModel.ConfirmForceReleaseCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsForceReleaseReasonPromptOpen);
        Assert.NotEmpty(viewModel.StatusMessage);
    }

    [Theory]
    [InlineData(null, HeldServerStatus.Pending, null, HeldOrderBadgeKind.RemotePending)]
    [InlineData(SharedHeldOrderPublicationStatus.PendingPublish, HeldServerStatus.Pending, null, HeldOrderBadgeKind.LocalPendingPublish)]
    [InlineData(SharedHeldOrderPublicationStatus.Published, null, null, HeldOrderBadgeKind.Published)]
    [InlineData(SharedHeldOrderPublicationStatus.Blocked, HeldServerStatus.Pending, null, HeldOrderBadgeKind.Blocked)]
    [InlineData(null, HeldServerStatus.Pending, LocalClaimStatus.Prepared, HeldOrderBadgeKind.LocalClaimPrepared)]
    [InlineData(null, HeldServerStatus.Pending, LocalClaimStatus.Active, HeldOrderBadgeKind.LocalClaimActive)]
    [InlineData(null, HeldServerStatus.Claimed, null, HeldOrderBadgeKind.ClaimedByOther)]
    [InlineData(null, HeldServerStatus.Completed, null, HeldOrderBadgeKind.Completed)]
    [InlineData(null, null, null, HeldOrderBadgeKind.LocalHold)]
    public void Held_badge_resolver_matches_status_combination(
        SharedHeldOrderPublicationStatus? publication,
        HeldServerStatus? serverStatus,
        LocalClaimStatus? claimStatus,
        HeldOrderBadgeKind expected)
    {
        Assert.Equal(expected, HeldOrderStatusResolver.Resolve(publication, serverStatus, claimStatus));
    }

    [Fact]
    public async Task Local_source_rows_keep_legacy_shape_without_held_metadata()
    {
        var orderGuid = Guid.NewGuid();
        var suspendedOrderGuid = Guid.NewGuid();
        var receiptQuery = new CapturingReceiptQueryService
        {
            Orders =
            [
                new LocalOrderSummary(
                    orderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero),
                    16m,
                    1m,
                    15m,
                    "Synced",
                    2,
                    "Cash")
            ]
        };
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    suspendedOrderGuid,
                    "S001",
                    "POS-02",
                    "Bob",
                    new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero),
                    12m,
                    0m,
                    12m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            sharedHeldOrderCoordinator: new CapturingSharedHeldOrderCoordinator(),
            sharedHeldOrderApiClient: new StubSharedHeldOrderApiClient(),
            sharedHeldOrderRepository: new CapturingSharedHeldOrderRepository());

        viewModel.DateFrom = new DateTime(2026, 5, 10);
        viewModel.DateTo = new DateTime(2026, 5, 10);
        await viewModel.LoadAsync();

        Assert.True(viewModel.IsLocalSourceSelected);
        Assert.Equal(2, viewModel.Orders.Count);
        Assert.All(viewModel.Orders, order =>
        {
            Assert.False(order.IsHeldOrder);
            Assert.False(order.CanForceRelease);
            Assert.Null(order.HeldClaimId);
            Assert.Equal(HeldOrderBadgeKind.LocalHold, order.HeldBadgeKind);
            Assert.False(order.CanRemoteRecall);
            Assert.False(order.CanOfflineRecall);
            Assert.False(order.CanLegacyRecall);
        });
        Assert.Equal(
            suspendedOrderGuid,
            viewModel.Orders.Single(order => order.IsSuspendedOrder).OrderGuid);
    }

    private static PosSessionState CreateSession(
        string storeCode = "S001",
        string storeName = "Main Store",
        string deviceCode = "POS-01",
        bool isOnline = true)
    {
        return new PosSessionState("HB POS", storeCode, storeName, deviceCode, "C001", "Alice", isOnline, 0);
    }

    private static InstallmentOrderSummary CreateInstallmentOrder(
        string orderNumber,
        string customerName,
        string phone,
        decimal paidAmount,
        decimal outstandingAmount)
    {
        return new InstallmentOrderSummary(
            Guid.NewGuid(),
            orderNumber,
            customerName,
            phone,
            paidAmount + outstandingAmount,
            20m,
            paidAmount,
            outstandingAmount,
            0,
            outstandingAmount > 0m,
            outstandingAmount == 0m,
            outstandingAmount > 0m,
            outstandingAmount > 0m,
            outstandingAmount > 0m ? "待补款" : "待提货",
            "POS-01",
            DateTimeOffset.Now);
    }

    private static LocalInstallmentOrder CreateLocalInstallmentOrder(InstallmentOrderSummary order)
    {
        return new LocalInstallmentOrder(
            order.OrderId,
            order.OrderId,
            order.OrderNumber,
            "S001",
            order.DeviceCode,
            "C001",
            "Alice",
            order.CustomerName,
            order.CustomerPhone,
            DateTimeOffset.Now.AddMinutes(-5),
            order.UpdatedAt,
            order.TotalAmount,
            20m,
            order.DownPaymentAmount,
            order.PaidAmount,
            order.OutstandingAmount,
            order.OutstandingAmount > 0m ? InstallmentStatus.Active : InstallmentStatus.PaidOff,
            [new InstallmentLineDto(Guid.NewGuid(), "P001", null, "Receipt Tea", "930001", 1m, order.TotalAmount, 0m, order.TotalAmount, "ITEM-INSTALLMENT")],
            [new InstallmentPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, order.PaidAmount, null, InstallmentPaymentStatus.Recorded, DateTimeOffset.Now, "C001", order.DeviceCode)],
            null);
    }

    private static SellableItemDto CatalogItem(
        string productCode,
        string lookupCode,
        string? itemNumber,
        string? barcode,
        string? productImage)
    {
        return new SellableItemDto(
            "S001",
            productCode,
            null,
            productCode,
            lookupCode,
            itemNumber,
            barcode,
            10m,
            PriceSourceKind.ProductBase,
            "Product",
            1m,
            DateTimeOffset.UtcNow,
            productImage);
    }

    private sealed class CapturingReceiptQueryService : IReceiptQueryService
    {
        public IReadOnlyList<LocalOrderSummary> Orders { get; set; } = [];

        public Dictionary<Guid, ReceiptDetails> Receipts { get; } = [];

        public Func<LocalOrderHistoryQuery, CancellationToken, Task<IReadOnlyList<LocalOrderSummary>>>? QueryHandler { get; set; }

        public Func<Guid, CancellationToken, Task<ReceiptDetails?>>? ReceiptHandler { get; set; }

        public int QueryCallCount { get; private set; }

        public int GetReceiptCallCount { get; private set; }

        public LocalOrderHistoryQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(new LocalOrderHistoryQuery(), take, cancellationToken);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            LastQuery = query;
            if (QueryHandler is not null)
            {
                return QueryHandler(query, cancellationToken);
            }

            return Task.FromResult(Orders);
        }

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            GetReceiptCallCount++;
            if (ReceiptHandler is not null)
            {
                return ReceiptHandler(orderGuid, cancellationToken);
            }

            return Task.FromResult(Receipts.TryGetValue(orderGuid, out var receipt) ? receipt : null);
        }

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Receipts.Values.FirstOrDefault());
        }
    }

    private sealed class TrackingRawScannerService : IRawScannerService, IScannerInputDeduplicator
    {
        private readonly Dictionary<string, Action<RawBarcodeScannedEventArgs>> _handlers = [];
        private readonly ScannerInputDuplicateGuard _duplicateGuard = new();

        public bool IsActive { get; private set; }

        public int SubscribeCount { get; private set; }

        public int UnsubscribeCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
        {
            SubscribeCount++;
            _handlers[pageId] = handler;
        }

        public void Unsubscribe(string pageId)
        {
            UnsubscribeCount++;
            _handlers.Remove(pageId);
        }

        public bool HasSubscription(string pageId) => _handlers.ContainsKey(pageId);

        public void Emit(string pageId, string barcode)
        {
            Assert.True(_handlers.TryGetValue(pageId, out var handler));
            var scannedAt = DateTimeOffset.UtcNow;
            Assert.True(_duplicateGuard.TryAccept(barcode, "raw", scannedAt));
            handler!(new RawBarcodeScannedEventArgs(barcode, "scanner-device", scannedAt));
        }

        bool IScannerInputDeduplicator.TryAcceptScanDelivery(
            string barcode,
            string source,
            DateTimeOffset timestamp) => _duplicateGuard.TryAccept(barcode, source, timestamp);

        public void SetActivePage(string? pageId)
        {
        }

        public void Start(IntPtr hwnd)
        {
            IsActive = true;
        }

        public void Stop()
        {
            IsActive = false;
        }

        public void ClearPendingInput()
        {
        }

        public Task ResetBindingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            IntPtr.Zero;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingSuspendedOrderService : ISuspendedOrderService
    {
        public IReadOnlyList<SuspendedOrderSummary> PendingOrders { get; set; } = [];

        public Dictionary<Guid, SuspendedOrder> Orders { get; } = [];

        public string? LastDeviceCode { get; private set; }

        public Guid? RecalledOrderGuid { get; private set; }

        public bool ThrowOnGetPendingOrders { get; set; }

        public Exception? GetOrderException { get; set; }

        public Func<Guid, CancellationToken, Task<SuspendedOrder?>>? GetOrderHandler { get; set; }

        public Task<SuspendedOrder> SuspendCurrentOrderAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingOrdersAsync(
            string storeCode,
            string? deviceCode = null,
            string? keyword = null,
            int take = 100,
            CancellationToken cancellationToken = default)
        {
            LastDeviceCode = deviceCode;
            if (ThrowOnGetPendingOrders)
            {
                throw new InvalidOperationException("simulated held local load failure");
            }

            return Task.FromResult(PendingOrders);
        }

        public Task<SuspendedOrder?> GetOrderAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default)
        {
            if (GetOrderHandler is not null)
            {
                return GetOrderHandler(suspendedOrderGuid, cancellationToken);
            }

            if (GetOrderException is not null)
            {
                return Task.FromException<SuspendedOrder?>(GetOrderException);
            }

            return Task.FromResult(Orders.TryGetValue(suspendedOrderGuid, out var order) ? order : null);
        }

        public Task<SuspendedOrder> RecallOrderAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default)
        {
            RecalledOrderGuid = suspendedOrderGuid;
            PendingOrders = [];
            return Task.FromResult(new SuspendedOrder(
                suspendedOrderGuid,
                "S001",
                "POS-01",
                "C001",
                "Alice",
                DateTimeOffset.Now,
                10m,
                0m,
                10m,
                SuspendedOrderStatus.Recalled,
                []));
        }
    }

    private sealed class CapturingRemoteOrderHistoryService : IRemoteOrderHistoryService
    {
        public RemoteOrderHistoryResult QueryResult { get; init; } = new([]);

        public Exception? QueryException { get; set; }

        public Func<RemoteOrderHistoryQuery, CancellationToken, Task<RemoteOrderHistoryResult>>? QueryHandler { get; set; }

        public Dictionary<Guid, ReceiptDetails> Receipts { get; } = [];

        public Func<Guid, CancellationToken, Task<ReceiptDetails?>>? DetailsHandler { get; set; }

        public RemoteOrderHistoryQuery? LastQuery { get; private set; }

        public Task<RemoteOrderHistoryResult> QueryAsync(RemoteOrderHistoryQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            if (QueryHandler is not null)
            {
                return QueryHandler(query, cancellationToken);
            }

            if (QueryException is not null)
            {
                return Task.FromException<RemoteOrderHistoryResult>(QueryException);
            }

            return Task.FromResult(QueryResult);
        }

        public Task<ReceiptDetails?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            if (DetailsHandler is not null)
            {
                return DetailsHandler(orderGuid, cancellationToken);
            }

            return Task.FromResult(Receipts.TryGetValue(orderGuid, out var receipt) ? receipt : null);
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrderReturnContextDto?>(null);
        }

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
            OrderReturnRecordCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OrderReturnRecordCreateResponse(request.ReturnOrderGuid, []));
        }
    }

    private sealed class CallbackOrderExecutor(
        Func<IReadOnlyCollection<Guid>, OrderUploadExecutionResult> execute,
        IReadOnlyList<Guid>? reuploadableOrderGuids = null) : IOrderUploadExecutionService
    {
        public IReadOnlyList<Guid> SelectedIds { get; private set; } = [];

        public List<IReadOnlyList<Guid>> SelectedBatches { get; } = [];

        public IReadOnlyList<Guid> ReuploadableOrderGuids { get; set; } = reuploadableOrderGuids ?? [];

        public (DateTimeOffset SoldFrom, DateTimeOffset SoldTo, string? DeviceCode)? LastReuploadableQuery { get; private set; }

        public TaskCompletionSource<OrderUploadExecutionResult>? SelectedExecutionCompletion { get; set; }

        public TaskCompletionSource SelectedExecutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SelectedCallCount { get; private set; }

        public Task<OrderUploadExecutionResult> ExecuteOneAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(execute([orderGuid]));

        public Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OrderUploadExecutionResult(0, 0, 0));

        public Task<OrderUploadExecutionResult> ExecuteSelectedAsync(
            IReadOnlyCollection<Guid> orderGuids,
            CancellationToken cancellationToken = default)
        {
            SelectedCallCount++;
            SelectedIds = orderGuids.ToArray();
            SelectedBatches.Add(SelectedIds);
            SelectedExecutionStarted.TrySetResult();
            if (SelectedExecutionCompletion is not null)
            {
                return SelectedExecutionCompletion.Task;
            }

            return Task.FromResult(execute(orderGuids));
        }

        public Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
            DateTimeOffset soldFrom,
            DateTimeOffset soldTo,
            string? deviceCode,
            CancellationToken cancellationToken = default)
        {
            LastReuploadableQuery = (soldFrom, soldTo, deviceCode);
            return Task.FromResult(ReuploadableOrderGuids);
        }
    }

    private sealed class CapturingConfirmationDialogService : IConfirmationDialogService
    {
        public bool Result { get; set; }

        public Task<bool>? OrderDateRangeResultTask { get; set; }

        public bool HeldOrderCancellationResult { get; set; }

        public int CallCount { get; private set; }

        public int HeldOrderCancellationCallCount { get; private set; }

        public int OrderCount { get; private set; }

        public int BatchCount { get; private set; }

        public Task<bool> ConfirmExitApplicationAsync() => throw new NotSupportedException();

        public Task<bool> ConfirmResetTestSalesDataAsync() => throw new NotSupportedException();

        public Task<bool> ConfirmInstallmentFullFirstPaymentAsync() => throw new NotSupportedException();

        public Task<bool> ConfirmInstallmentPickupAfterPaidOffAsync() => throw new NotSupportedException();

        public Task<bool> ConfirmLinklySettlementAsync(DateTime businessDate) => throw new NotSupportedException();

        public Task<bool> ConfirmHeldOrderCancellationAsync()
        {
            HeldOrderCancellationCallCount++;
            return Task.FromResult(HeldOrderCancellationResult);
        }

        public Task<bool> ConfirmOrderDateRangeReuploadAsync(
            int orderCount,
            int batchCount,
            DateTime dateFrom,
            DateTime dateTo)
        {
            CallCount++;
            OrderCount = orderCount;
            BatchCount = batchCount;
            return OrderDateRangeResultTask ?? Task.FromResult(Result);
        }
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    private sealed class CapturingInstallmentOrderService : IInstallmentOrderService
    {
        public IReadOnlyList<InstallmentOrderSummary> Orders { get; init; } = [];

        public InstallmentHistorySearchQuery? LastHistoryQuery { get; private set; }

        public Dictionary<Guid, LocalInstallmentOrder> LocalOrders { get; } = [];

        public Guid? LastConfirmPickupOrderId { get; private set; }

        public int ConfirmPickupCallCount { get; private set; }

        public Exception? ConfirmPickupException { get; init; }

        public InstallmentOrderActionResult? ConfirmPickupResult { get; init; }

        public IReadOnlySet<Guid> LockedInstallments { get; init; } = new HashSet<Guid>();

        public Exception? LockedInstallmentsException { get; init; }

        public LocalInstallmentOrder? OrderDetails { get; init; }

        public Exception? OrderDetailsException { get; init; }

        public Func<PosSessionState, Guid, CancellationToken, Task<LocalInstallmentOrder?>>? OrderDetailsHandler { get; init; }

        public Task<IReadOnlySet<Guid>> GetLockedInstallmentGuidsAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            return LockedInstallmentsException is null
                ? Task.FromResult(LockedInstallments)
                : Task.FromException<IReadOnlySet<Guid>>(LockedInstallmentsException);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Orders);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Orders);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> QueryHistoryAsync(
            PosSessionState session,
            InstallmentHistorySearchQuery query,
            CancellationToken cancellationToken = default)
        {
            LastHistoryQuery = query;
            return Task.FromResult(Orders);
        }

        public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LocalOrders.TryGetValue(installmentGuid, out var order) ? order : null);
        }

        public Task<LocalInstallmentOrder?> GetOrderDetailsAsync(
            PosSessionState session,
            Guid installmentGuid,
            CancellationToken cancellationToken = default)
        {
            if (OrderDetailsHandler is not null)
            {
                return OrderDetailsHandler(session, installmentGuid, cancellationToken);
            }

            if (OrderDetailsException is not null)
            {
                return Task.FromException<LocalInstallmentOrder?>(OrderDetailsException);
            }

            return Task.FromResult(OrderDetails ?? (LocalOrders.TryGetValue(installmentGuid, out var order) ? order : null));
        }

        public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            ConfirmPickupCallCount++;
            LastConfirmPickupOrderId = orderId;
            if (ConfirmPickupException is not null)
            {
                return Task.FromException<InstallmentOrderActionResult>(ConfirmPickupException);
            }

            var order = Orders.FirstOrDefault(order => order.OrderId == orderId);
            return Task.FromResult(ConfirmPickupResult ?? new InstallmentOrderActionResult(order is not null, order is null ? "missing" : "confirmed", order));
        }
    }

    private static HistoryOrderListItem HeldHistoryRow(
        Guid holdGuid,
        bool canRecall = false,
        bool canRemoteRecall = false,
        bool canDeleteHeldOrder = false,
        bool canShare = false,
        Guid? heldClaimId = null,
        bool canForceRelease = false)
    {
        return new HistoryOrderListItem(
            holdGuid,
            TransactionHistorySource.HeldOrders,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            1,
            "Cash",
            "Held",
            IsSuspendedOrder: true,
            CanRecall: canRecall,
            IsHeldOrder: true,
            HeldClaimId: heldClaimId,
            CanForceRelease: canForceRelease,
            CanRemoteRecall: canRemoteRecall,
            CanDeleteHeldOrder: canDeleteHeldOrder,
            CanShare: canShare);
    }

    private static SuspendedOrderSummary HeldSummary(
        Guid guid,
        string deviceCode,
        decimal total,
        decimal discount,
        decimal actual,
        int lineCount)
    {
        var fixtureTime = HeldFixtureTime;
        return new SuspendedOrderSummary(
            guid,
            "S001",
            deviceCode,
            "Alice",
            fixtureTime,
            total,
            discount,
            actual,
            lineCount,
            SuspendedOrderStatus.Pending);
    }

    private static SharedHeldOrderPublication HeldPublication(
        Guid guid,
        SharedHeldOrderPublicationStatus status,
        string? errorCode = null,
        string? errorMessage = null,
        string? shareRequestedAtIso = null)
    {
        var fixtureTimeIso = HeldFixtureTimeIso;
        return new SharedHeldOrderPublication(
            guid,
            "S001",
            "POS-01",
            status,
            1,
            0,
            errorCode,
            errorMessage,
            null,
            fixtureTimeIso,
            fixtureTimeIso,
            fixtureTimeIso,
            ShareRequestedAtIso: shareRequestedAtIso);
    }

    private static SharedHeldOrderListItemDto HeldItem(
        Guid guid,
        long totalCents,
        long discountCents,
        long actualCents,
        int lineCount,
        string deviceCode = "POS-01")
    {
        var heldAt = HeldFixtureTime;
        return new SharedHeldOrderListItemDto(
            guid,
            "S001",
            deviceCode,
            "C001",
            "Alice",
            heldAt,
            heldAt.AddMinutes(5),
            lineCount,
            totalCents,
            discountCents,
            actualCents,
            1L);
    }

    private static SharedHeldOrderClaimRecovery HeldRecoveryClaim(
        Guid holdGuid,
        Guid claimGuid,
        LocalClaimStatus status)
    {
        var fixtureTimeIso = HeldFixtureTimeIso;
        return new SharedHeldOrderClaimRecovery(
            claimGuid,
            holdGuid,
            "S001",
            "POS-01",
            SharedHeldOrderClaimSource.RemoteClaim,
            status,
            $"prepare:{claimGuid:D}",
            null,
            null,
            SampleCanonical(),
            1L,
            null,
            null,
            null,
            fixtureTimeIso,
            fixtureTimeIso);
    }

    private sealed class CapturingSharedHeldOrderCoordinator : ISharedHeldOrderCoordinator
    {
        public List<(Guid HoldGuid, PosSessionState Session)> RemoteTakes { get; } = [];

        public List<(Guid HoldGuid, PosSessionState Session)> LocalRecalls { get; } = [];

        public int ReconcileCalls { get; private set; }

        public List<PosSessionState> LocalRecoveries { get; } = [];

        public List<(Guid HoldGuid, Guid ClaimGuid, string Reason, PosSessionState Session)> ForceReleases { get; } = [];

        public Func<Guid, PosSessionState, Task<SharedHeldOrderTakeResult>>? TakeRemoteHandler { get; set; }

        public Func<Guid, Guid, string, PosSessionState, Task>? ForceReleaseHandler { get; set; }

        public Task<SharedHeldOrderTakeResult> TakeRemoteHoldAsync(
            Guid holdGuid,
            PosSessionState session,
            Guid? claimGuid = null,
            CancellationToken cancellationToken = default)
        {
            RemoteTakes.Add((holdGuid, session));
            return TakeRemoteHandler?.Invoke(holdGuid, session)
                ?? Task.FromResult(new SharedHeldOrderTakeResult(
                    Guid.NewGuid(),
                    holdGuid,
                    RestoredToCart: true));
        }

        public Task<SharedHeldOrderTakeResult> RecallLocalPublicationAsync(
            Guid localHoldGuid,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            LocalRecalls.Add((localHoldGuid, session));
            return Task.FromResult(new SharedHeldOrderTakeResult(
                Guid.NewGuid(),
                localHoldGuid,
                RestoredToCart: true));
        }

        public Task<SharedHeldOrderReconcileResult> ReconcileClaimsAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            ReconcileCalls++;
            return Task.FromResult(new SharedHeldOrderReconcileResult([], [], []));
        }

        public Task<SharedHeldOrderLocalRecoveryResult> RecoverLocalClaimsAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            LocalRecoveries.Add(session);
            return Task.FromResult(new SharedHeldOrderLocalRecoveryResult([], []));
        }

        public Task ReleaseActiveClaimAsync(
            Guid claimGuid,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ForceReleaseAsync(
            Guid holdGuid,
            Guid claimGuid,
            string reason,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            ForceReleases.Add((holdGuid, claimGuid, reason, session));
            return ForceReleaseHandler?.Invoke(holdGuid, claimGuid, reason, session)
                ?? Task.CompletedTask;
        }
    }

    private sealed class CapturingSharedHeldOrderPublicationWorker : ISharedHeldOrderPublicationWorker
    {
        public List<(string StoreCode, string? DeviceCode)> Runs { get; } = [];

        public Exception? RunException { get; init; }

        public Task<SharedHeldOrderPublicationRunResult> RunOnceAsync(
            string storeCode,
            string? deviceCode = null,
            CancellationToken cancellationToken = default)
        {
            Runs.Add((storeCode, deviceCode));
            return RunException is null
                ? Task.FromResult(new SharedHeldOrderPublicationRunResult(0, 0, 0, 0, 0, 0))
                : Task.FromException<SharedHeldOrderPublicationRunResult>(RunException);
        }
    }

    private sealed class CapturingSharedHeldOrderRepository : ISharedHeldOrderRepository
    {
        public Dictionary<Guid, SharedHeldOrderPublication> Publications { get; } = [];

        public IReadOnlyList<SharedHeldOrderClaimRecovery> Claims { get; init; } = [];

        public List<(Guid HoldGuid, string StoreCode, string DeviceCode, string Timestamp)> DeleteStages { get; } = [];

        public List<(Guid HoldGuid, string StoreCode, string DeviceCode, string Timestamp)> DeleteCompletions { get; } = [];

        public List<(Guid HoldGuid, string StoreCode, string DeviceCode, string RequestedAt)> ShareRequests { get; } = [];

        public Func<Guid, string, string, string, SharedHeldOrderDeleteStage?>? StageDelete { get; set; }

        public Func<Guid, string, string, string, bool>? CompleteDelete { get; set; }

        public Func<Guid, string, string, string, SharedHeldOrderShareRequestResult>? RequestShare { get; set; }

        public Func<Guid, SharedHeldOrderCanonicalPayload?>? PublicationPayload { get; set; }

        public Task<SharedHeldOrderShareRequestResult> TryRequestShareAsync(
            Guid holdGuid,
            string storeCode,
            string deviceCode,
            string requestedAtIso,
            CancellationToken cancellationToken = default)
        {
            ShareRequests.Add((holdGuid, storeCode, deviceCode, requestedAtIso));
            return Task.FromResult(
                RequestShare?.Invoke(holdGuid, storeCode, deviceCode, requestedAtIso)
                ?? SharedHeldOrderShareRequestResult.NotFound);
        }

        public Task<IReadOnlyList<SuspendedOrder>> ListLegacyOrdersNeedingEvaluationAsync(
            string storeCode,
            string? deviceCode = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SuspendedOrder>>([]);
        }

        public Task<SharedHeldOrderPublication?> GetPublicationAsync(
            Guid localHoldGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Publications.GetValueOrDefault(localHoldGuid));
        }

        public Task<SharedHeldOrderDeleteStage?> TryStageDeletePendingAsync(
            Guid holdGuid,
            string storeCode,
            string deviceCode,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            DeleteStages.Add((holdGuid, storeCode, deviceCode, updatedAtIso));
            return Task.FromResult(StageDelete?.Invoke(holdGuid, storeCode, deviceCode, updatedAtIso));
        }

        public Task<bool> TryCompleteDeletePendingAsync(
            Guid holdGuid,
            string storeCode,
            string deviceCode,
            string completedAtIso,
            CancellationToken cancellationToken = default)
        {
            DeleteCompletions.Add((holdGuid, storeCode, deviceCode, completedAtIso));
            return Task.FromResult(CompleteDelete?.Invoke(holdGuid, storeCode, deviceCode, completedAtIso) ?? false);
        }

        public Task<IReadOnlyList<SharedHeldOrderClaimRecovery>> FindRecoverableClaimsAsync(
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Claims);
        }

        public Task<bool> TryExpirePreparedRemoteClaimAsync(
            Guid claimId,
            string releaseIdempotencyKey,
            string nowIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> UpsertPublicationAsync(
            Guid localHoldGuid,
            string storeCode,
            string deviceCode,
            SharedHeldOrderPublicationStatus status,
            byte[]? payloadCiphertext,
            string heldAtIso,
            string createdAtIso,
            string updatedAtIso,
            string? errorCode = null,
            string? errorMessage = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SharedHeldOrderPublication>> ListDuePublicationsAsync(
            string nowIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryAdvancePublicationAsync(
            Guid localHoldGuid,
            SharedHeldOrderPublicationStatus expectedStatus,
            int expectedRevision,
            SharedHeldOrderPublicationStatus newStatus,
            string updatedAtIso,
            string? errorCode = null,
            string? errorMessage = null,
            string? lastAttemptAtIso = null,
            string? nextAttemptAtIso = null,
            long? remoteRevision = null,
            string? remoteUpdatedAtIso = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryStagePendingPublishAsync(
            Guid localHoldGuid,
            int expectedRevision,
            SharedHeldOrderCanonicalPayload payload,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryBlockPublicationAsync(
            Guid localHoldGuid,
            int expectedRevision,
            string errorCode,
            string errorMessage,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SharedHeldOrderCanonicalPayload?> GetPublicationPayloadAsync(
            Guid localHoldGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PublicationPayload?.Invoke(localHoldGuid));
        }

        public Task<bool> TrySavePreparedClaimAsync(
            SharedHeldOrderClaimDraft draft,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryActivateClaimAsync(
            Guid claimId,
            string prepareIdempotencyKey,
            string activateIdempotencyKey,
            long? serverRevision,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryBindOrderAsync(
            Guid claimId,
            string activateIdempotencyKey,
            string boundOrderGuid,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryCompleteClaimAsync(
            Guid claimId,
            string activateIdempotencyKey,
            string releaseIdempotencyKey,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryReleaseClaimAsync(
            Guid claimId,
            string releaseIdempotencyKey,
            LocalClaimStatus expectedStatus,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryForceReleaseClaimAsync(
            Guid claimId,
            string releaseIdempotencyKey,
            LocalClaimStatus expectedStatus,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TrySupersedeClaimAsync(
            Guid claimId,
            string supersedeIdempotencyKey,
            LocalClaimStatus expectedStatus,
            string updatedAtIso,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SharedHeldOrderClaimRecord?> GetClaimAsync(
            Guid claimId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, period);
            Timers.Add(timer);
            return timer;
        }

        public sealed class ManualTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public ManualTimer(
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _callback = callback;
                _state = state;
                CreatedDueTime = dueTime;
                CreatedPeriod = period;
            }

            public TimeSpan CreatedDueTime { get; }

            public TimeSpan CreatedPeriod { get; }

            public bool IsDisposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                return !IsDisposed;
            }

            public void Dispose()
            {
                IsDisposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (!IsDisposed)
                {
                    _callback(_state);
                }
            }
        }
    }
}
