using System.Diagnostics;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class PosCoreTests
{
    [Fact]
    public void Local_price_index_prefers_exact_barcode_then_keyword_matches()
    {
        var index = new LocalSellableItemIndex();

        var barcodeItem = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m);
        var keywordItem = CreateItem("SKU-002", "Apple Juice", "690002", PriceSourceKind.ProductBase, 8.8m);
        index.ReplaceAll([keywordItem, barcodeItem]);

        var barcodeMatches = index.Search("690001");
        var keywordMatches = index.Search("juice");

        Assert.Equal("SKU-001", Assert.Single(barcodeMatches).ProductCode);
        Assert.Equal("SKU-002", Assert.Single(keywordMatches).ProductCode);
    }

    [Fact]
    public void Local_price_index_exact_lookup_matches_lookup_code_within_store()
    {
        var index = new LocalSellableItemIndex();
        var barcodeItem = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m, itemNumber: "ITEM-001");
        var productCodeItem = CreateItem("SKU-002", "Apple Juice", "690002", PriceSourceKind.ProductBase, 8.8m, itemNumber: "ITEM-002");
        var otherStoreItem = CreateItem("SKU-003", "Other Store Milk", "690001", PriceSourceKind.ProductBase, 9.9m, storeCode: "S002");
        index.ReplaceAll([barcodeItem, productCodeItem, otherStoreItem]);

        Assert.Equal("SKU-001", Assert.Single(index.FindExactMatches("S001", "690001")).ProductCode);
        Assert.Equal("SKU-003", Assert.Single(index.FindExactMatches("S002", "690001")).ProductCode);
        Assert.Empty(index.FindExactMatches("S001", "ITEM-001"));
        Assert.Empty(index.FindExactMatches("S001", "sku-002"));
    }

    [Fact]
    public async Task Local_price_index_exact_lookup_async_matches_sync_and_honors_cancellation()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m)]);

        var expected = index.FindExactMatches("S001", "690001");
        var actual = await index.FindExactMatchesAsync("S001", "690001", CancellationToken.None);

        Assert.Equal(expected, actual);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            index.FindExactMatchesAsync("S001", "690001", cancellationSource.Token));
    }

    [Fact]
    public async Task Local_price_index_exact_lookup_async_cancels_after_worker_blocks_on_gate()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m)]);
        var gate = typeof(LocalSellableItemIndex)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(index);
        Assert.NotNull(gate);

        var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseGate = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            Monitor.Enter(gate);
            try
            {
                gateHeld.TrySetResult();
                releaseGate.Wait();
            }
            finally
            {
                Monitor.Exit(gate);
            }
        });
        try
        {
            await gateHeld.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            index.ExactLookupGateWaitForTests = () => workerBlocked.TrySetResult();
            using var cancellationSource = new CancellationTokenSource();
            var lookupTask = index.FindExactMatchesAsync("S001", "690001", cancellationSource.Token);
            await workerBlocked.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                lookupTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout));
        }
        finally
        {
            index.ExactLookupGateWaitForTests = null;
            releaseGate.Set();
            await holder.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }

        Assert.Single(index.FindExactMatches("S001", "690001"));
        Assert.Single(await index.FindExactMatchesAsync("S001", "690001", CancellationToken.None));
    }

    [Fact]
    public void Local_price_index_search_can_filter_by_store()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-001", "Shared Milk", "690001", PriceSourceKind.StoreRetailPrice, 12.5m, storeCode: "S001"),
            CreateItem("SKU-002", "Shared Milk", "690001", PriceSourceKind.StoreRetailPrice, 9.9m, storeCode: "S002")
        ]);

        var matches = index.Search("S002", "milk");

        var item = Assert.Single(matches);
        Assert.Equal("S002", item.StoreCode);
        Assert.Equal("SKU-002", item.ProductCode);
    }

    [Fact]
    public void Local_price_index_exact_lookup_deduplicates_same_item_code_aliases()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("690001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m, itemNumber: "690001")]);

        var matches = index.FindExactMatches("S001", "690001");

        Assert.Single(matches);
    }

    [Fact]
    public void Local_price_index_upsert_replaces_only_matching_store_lookup_and_aliases()
    {
        var index = new LocalSellableItemIndex();
        var localItem = CreateItem("SKU-OLD", "Zulu Local", "690010", PriceSourceKind.StoreRetailPrice, 5m, itemNumber: "ITEM-OLD");
        var sibling = CreateItem("SKU-SIBLING", "Bravo Item", "690011", PriceSourceKind.StoreRetailPrice, 6m);
        var otherStore = CreateItem("SKU-OTHER", "Other Store", "690010", PriceSourceKind.StoreRetailPrice, 7m, storeCode: "S002");
        index.ReplaceAll([localItem, sibling, otherStore]);
        var updatedItem = localItem with
        {
            ProductCode = "SKU-NEW",
            DisplayName = "Alpha Local",
            ItemNumber = "ITEM-NEW",
            Barcode = "BAR-NEW",
            RetailPrice = 8m
        };

        index.Upsert(updatedItem);

        Assert.Same(updatedItem, Assert.Single(index.FindExactMatches("S001", "690010")));
        Assert.Same(otherStore, Assert.Single(index.FindExactMatches("S002", "690010")));
        Assert.Empty(index.FindMetadataExactMatches("S001", "ITEM-OLD"));
        Assert.Empty(index.FindMetadataExactMatches("S001", "SKU-OLD"));
        Assert.Same(updatedItem, Assert.Single(index.FindMetadataExactMatches("S001", "ITEM-NEW")));
        Assert.Same(updatedItem, Assert.Single(index.FindMetadataExactMatches("S001", "SKU-NEW")));
        Assert.Same(updatedItem, Assert.Single(index.FindMetadataExactMatches("S001", "BAR-NEW")));
        Assert.Equal(["Alpha Local", "Bravo Item", "Other Store"], index.Items.Select(item => item.DisplayName));
    }

    [Fact]
    public void Local_price_index_remove_lookup_clears_aliases_only_for_matching_store()
    {
        var index = new LocalSellableItemIndex();
        var localItem = CreateItem("SKU-REMOVE", "Remove Local", "690020", PriceSourceKind.StoreRetailPrice, 5m, itemNumber: "ITEM-REMOVE");
        var otherStore = CreateItem("SKU-KEEP", "Keep Other Store", "690020", PriceSourceKind.StoreRetailPrice, 7m, storeCode: "S002");
        index.ReplaceAll([localItem, otherStore]);

        Assert.True(index.RemoveLookup(" s001 ", " 690020 "));

        Assert.Empty(index.FindExactMatches("S001", "690020"));
        Assert.Empty(index.FindMetadataExactMatches("S001", "ITEM-REMOVE"));
        Assert.Empty(index.FindMetadataExactMatches("S001", "SKU-REMOVE"));
        Assert.Same(otherStore, Assert.Single(index.FindExactMatches("S002", "690020")));
        Assert.False(index.RemoveLookup("S001", "690020"));
        Assert.Same(otherStore, Assert.Single(index.Items));
    }

    [Fact]
    public void Local_price_index_single_upsert_does_not_allocate_a_full_large_index()
    {
        const int itemCount = 100_000;
        var index = new LocalSellableItemIndex();
        var items = Enumerable.Range(0, itemCount)
            .Select(itemIndex => CreateItem(
                $"SKU-{itemIndex:D6}",
                $"Item {itemIndex:D6}",
                $"LOOKUP-{itemIndex:D6}",
                PriceSourceKind.StoreRetailPrice,
                1m))
            .ToArray();
        index.ReplaceAll(items);
        var warmItem = items[itemCount / 2] with { RetailPrice = 2m };
        index.Upsert(warmItem);
        var replacement = warmItem with { RetailPrice = 3m };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        index.Upsert(replacement);

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(allocatedBytes < 5_000_000, $"Single upsert allocated {allocatedBytes:N0} bytes.");
        Assert.Same(replacement, Assert.Single(index.FindExactMatches("S001", replacement.LookupCode)));
    }

    [Fact]
    public void Cart_adds_scanned_item_and_calculates_totals()
    {
        var cart = new PosCartService();
        var item = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m);

        cart.AddItem(item);
        cart.AddItem(item);

        Assert.Equal(2, cart.Lines[0].Quantity);
        Assert.Equal(25m, cart.TotalAmount);
        Assert.Equal(25m, cart.ActualAmount);
    }

    [Fact]
    public void Cash_checkout_creates_order_snapshot_and_change()
    {
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var item = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreClearancePrice, 9.9m, itemNumber: "ITEM-001");

        cart.AddItem(item);
        var result = checkout.CreateCashOrder(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            tenderedAmount: 10m);

        Assert.Equal(9.9m, result.Order.ActualAmount);
        Assert.Equal(0.1m, result.ChangeAmount);
        var line = Assert.Single(result.Order.Lines);
        Assert.Equal("Milk 1L", line.DisplayName);
        Assert.Equal("ITEM-001", line.ItemNumber);
        Assert.Equal(PriceSourceKind.StoreClearancePrice, result.Order.Lines[0].PriceSource);
        Assert.Equal(OrderLineKind.Sale, line.Kind);
        Assert.Equal(9.9m, Assert.Single(result.Order.Payments).Amount);
    }

    [Fact]
    public void Cash_checkout_allows_zero_total_order_without_payments()
    {
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        cart.AddItem(CreateItem("SKU-ZERO-TOTAL", "Zero Total", "690120", PriceSourceKind.StoreRetailPrice, 5m));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-RETURN",
            null,
            "Returned Zero Total",
            "690121",
            "ITEM-RETURN",
            null,
            1m,
            5m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-1",
            Guid.NewGuid(),
            Guid.NewGuid()));

        var result = checkout.CreatePaymentOrder(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            [],
            cashTenderedAmount: 0m);

        Assert.Equal(0m, result.Order.ActualAmount);
        Assert.Empty(result.Order.Payments);
        Assert.Equal(0m, result.TenderedAmount);
        Assert.Equal(0m, result.ChangeAmount);
    }

    [Fact]
    public void Cash_checkout_preserves_return_metadata_and_negative_cash_payment_for_refund()
    {
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var originalOrderGuid = Guid.NewGuid();
        var originalOrderLineGuid = Guid.NewGuid();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-RET-1",
            "REF-1",
            "Returned Tea",
            "690122",
            "ITEM-RET-1",
            null,
            1m,
            7.82m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-SOURCE-1",
            originalOrderGuid,
            originalOrderLineGuid));

        var result = checkout.CreatePaymentOrder(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            [new PaymentTender(PaymentMethodKind.Cash, -7.80m)],
            cashTenderedAmount: -7.80m);

        var line = Assert.Single(result.Order.Lines);
        var payment = Assert.Single(result.Order.Payments);
        Assert.Equal(OrderLineKind.Return, line.Kind);
        Assert.Equal("RETURN-SOURCE-1", line.ReturnSourceKey);
        Assert.Equal(originalOrderGuid, line.OriginalOrderGuid);
        Assert.Equal(originalOrderLineGuid, line.OriginalOrderDetailGuid);
        Assert.Equal(-7.80m, payment.Amount);
        Assert.Equal(-7.80m, result.TenderedAmount);
        Assert.Equal(0m, result.ChangeAmount);
    }

    [Fact]
    public void Cash_checkout_rejects_zero_price_cart_lines()
    {
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        cart.AddItem(CreateItem("SKU-ZERO", "Zero Tea", "690099", PriceSourceKind.StoreRetailPrice, 0m));

        Assert.Throws<InvalidOperationException>(() => checkout.CreateCashOrder(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            tenderedAmount: 0m));
    }

    [Fact]
    public void Cash_checkout_rejects_non_integer_cart_quantities()
    {
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var line = cart.AddItem(CreateItem("SKU-FRACTION", "Fraction Tea", "690098", PriceSourceKind.StoreRetailPrice, 4m));
        SetUnsafeQuantity(line, 1.5m);

        Assert.Throws<InvalidOperationException>(() => checkout.CreateCashOrder(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            tenderedAmount: 10m));
    }

    [Fact]
    public async Task Pos_terminal_sync_refreshes_online_state_before_syncing()
    {
        var index = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var item = CreateItem("SKU-001", "Milk 1L", "690001", PriceSourceKind.StoreRetailPrice, 12.5m);
        var refreshedOnline = false;
        var synced = false;
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", false, 0),
            onOpenPayment: null,
            refreshOnlineAsync: _ =>
            {
                refreshedOnline = true;
                return Task.FromResult(true);
            },
            syncCatalogAsync: _ =>
            {
                synced = true;
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([item]);
            });

        await viewModel.SyncCommand.ExecuteAsync(null);

        Assert.True(refreshedOnline);
        Assert.True(synced);
        Assert.True(viewModel.Session.IsOnline);
        Assert.Equal("Catalog sync completed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Local_repositories_initialize_catalog_order_and_sync_queue()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var catalog = new LocalCatalogRepository(store);
            var orders = new LocalOrderRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var item = CreateItem("SKU-003", "Orange", "690003", PriceSourceKind.ProductBase, 3.2m);
            var cart = new PosCartService();
            var checkout = new CashCheckoutService();

            await schema.InitializeAsync();
            await catalog.ReplaceSellableItemsAsync([item]);

            var cachedItems = await catalog.LoadSellableItemsAsync();
            cart.AddItem(Assert.Single(cachedItems));
            var result = checkout.CreateCashOrder(
                cart,
                new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
                tenderedAmount: 5m);

            await orders.SavePendingOrderAsync(result.Order);

            Assert.Equal("SKU-003", cachedItems[0].ProductCode);
            Assert.Equal(1, await syncQueue.CountPendingAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Local_order_repository_reads_recent_order_with_lines_and_payments()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var recentOrders = await orders.GetRecentOrdersAsync();
            var savedOrder = await orders.GetOrderAsync(order.OrderGuid);

            var summary = Assert.Single(recentOrders);
            Assert.Equal(order.OrderGuid, summary.OrderGuid);
            Assert.Equal(2, summary.LineCount);
            Assert.Equal("Cash", summary.PaymentSummary);
            Assert.NotNull(savedOrder);
            Assert.Equal(2, savedOrder.Lines.Count);
            Assert.Equal("ITEM-101", savedOrder.Lines[0].ItemNumber);
            Assert.Equal(order.ActualAmount, Assert.Single(savedOrder.Payments).Amount);
            Assert.Equal(order.TenderedAmount, savedOrder.TenderedAmount);
            Assert.Equal(order.ChangeAmount, savedOrder.ChangeAmount);
            Assert.Equal(OrderLineKind.Return, savedOrder.Lines[1].Kind);
            Assert.Equal("RETURN-SOURCE-ORDER", savedOrder.Lines[1].ReturnSourceKey);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Local_schema_service_adds_tendered_and_change_columns_without_data_loss()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);

            await using (var connection = await store.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE LocalOrders (
                        OrderGuid TEXT PRIMARY KEY,
                        StoreCode TEXT NOT NULL,
                        DeviceCode TEXT NOT NULL,
                        CashierId TEXT NOT NULL,
                        CashierName TEXT NOT NULL,
                        SoldAt TEXT NOT NULL,
                        TotalAmount TEXT NOT NULL,
                        DiscountAmount TEXT NOT NULL,
                        ActualAmount TEXT NOT NULL,
                        SyncStatus TEXT NOT NULL
                    );

                    INSERT INTO LocalOrders
                    (OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt, TotalAmount, DiscountAmount, ActualAmount, SyncStatus)
                    VALUES
                    ('11111111-2222-3333-4444-555555555555', 'S001', 'POS-01', 'C001', 'Alice', '2026-05-24T09:30:00.0000000+00:00', '10.00', '1.00', '9.00', 'Pending');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();

            await using var verifyConnection = await store.OpenConnectionAsync();
            await using var pragmaCommand = verifyConnection.CreateCommand();
            pragmaCommand.CommandText = "PRAGMA table_info(LocalOrders);";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await pragmaCommand.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }
            }

            Assert.Contains("TenderedAmount", columns);
            Assert.Contains("ChangeAmount", columns);

            await using var dataCommand = verifyConnection.CreateCommand();
            dataCommand.CommandText = """
                SELECT TotalAmount, TenderedAmount, ChangeAmount
                FROM LocalOrders
                WHERE OrderGuid = '11111111-2222-3333-4444-555555555555';
                """;
            await using var dataReader = await dataCommand.ExecuteReaderAsync();
            Assert.True(await dataReader.ReadAsync());
            Assert.Equal("10.00", dataReader.GetString(0));
            Assert.True(dataReader.IsDBNull(1));
            Assert.True(dataReader.IsDBNull(2));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Transaction_history_view_model_loads_at_least_one_order()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var viewModel = new TransactionHistoryViewModel(orders);
            await viewModel.LoadAsync();
            await WaitUntilAsync(() => !viewModel.IsReceiptPreviewLoading);

            Assert.NotEmpty(viewModel.Orders);
            Assert.Equal(order.OrderGuid, viewModel.SelectedOrder?.OrderGuid);
            Assert.Equal(order.ActualAmount, viewModel.PreviewTotal);
            Assert.Equal(2, viewModel.ReceiptLines.Count);
            Assert.Single(viewModel.Payments);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Local_order_repository_filters_recent_orders_by_date_device_and_item_number_keyword()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-history-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var start = new DateTimeOffset(2026, 5, 24, 0, 0, 0, TimeSpan.Zero);
            var matchingOrder = CreateLocalOrder(
                orderGuid: Guid.Parse("11111111-2222-3333-4444-555555555555"),
                deviceCode: "POS-01",
                soldAt: start.AddHours(10),
                firstLookupCode: "930101",
                firstItemNumber: "ITEM-101");
            var nonMatchingDevice = CreateLocalOrder(
                orderGuid: Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"),
                deviceCode: "POS-02",
                soldAt: start.AddHours(11),
                firstLookupCode: "930201",
                firstItemNumber: "ITEM-201");
            var nonMatchingDate = CreateLocalOrder(
                orderGuid: Guid.Parse("99999999-8888-7777-6666-aaaaaaaaaaaa"),
                deviceCode: "POS-01",
                soldAt: start.AddDays(-2),
                firstLookupCode: "930301",
                firstItemNumber: "ITEM-301");

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(matchingOrder);
            await orders.SavePendingOrderAsync(nonMatchingDevice);
            await orders.SavePendingOrderAsync(nonMatchingDate);

            var filtered = await orders.GetRecentOrdersAsync(
                new LocalOrderHistoryQuery(
                    SoldFrom: start,
                    SoldTo: start.AddDays(1).AddTicks(-1),
                    DeviceCode: "pos-01",
                    Keyword: "item-101"));
            var byShortOrderId = await orders.GetRecentOrdersAsync(
                new LocalOrderHistoryQuery(Keyword: matchingOrder.OrderGuid.ToString("N")[..8]));
            var byLookupCode = await orders.GetRecentOrdersAsync(
                new LocalOrderHistoryQuery(Keyword: "930101"));
            // 订单短码（小票条码、历史页显示的）是 GUID 末 8 位，输入它也要能查到。
            var byLastEight = await orders.GetRecentOrdersAsync(
                new LocalOrderHistoryQuery(Keyword: ReceiptOrderShortCode.Create(matchingOrder.OrderGuid).ToLowerInvariant()));

            Assert.Equal(matchingOrder.OrderGuid, Assert.Single(filtered).OrderGuid);
            Assert.Equal(matchingOrder.OrderGuid, Assert.Single(byLastEight).OrderGuid);
            Assert.Equal(matchingOrder.OrderGuid, Assert.Single(byShortOrderId).OrderGuid);
            Assert.Equal(matchingOrder.OrderGuid, Assert.Single(byLookupCode).OrderGuid);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Local_schema_service_migrates_existing_orders_with_tendered_and_change_columns()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-order-migration-{Guid.NewGuid():N}.db");

        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE LocalOrders (
                        OrderGuid TEXT PRIMARY KEY,
                        StoreCode TEXT NOT NULL,
                        DeviceCode TEXT NOT NULL,
                        CashierId TEXT NOT NULL,
                        CashierName TEXT NOT NULL,
                        SoldAt TEXT NOT NULL,
                        TotalAmount TEXT NOT NULL,
                        DiscountAmount TEXT NOT NULL,
                        ActualAmount TEXT NOT NULL,
                        SyncStatus TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var schema = new LocalSchemaService(new LocalSqliteStore(databasePath));
            await schema.InitializeAsync();

            await using var verifyConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "PRAGMA table_info(LocalOrders);";
            await using var reader = await verifyCommand.ExecuteReaderAsync();

            var columns = new List<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("TenderedAmount", columns);
            Assert.Contains("ChangeAmount", columns);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Local_schema_service_migrates_existing_order_lines_with_return_columns()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-client-migration-{Guid.NewGuid():N}.db");

        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE LocalOrderLines (
                        OrderLineGuid TEXT PRIMARY KEY,
                        OrderGuid TEXT NOT NULL,
                        ProductCode TEXT NOT NULL,
                        ReferenceCode TEXT NULL,
                        DisplayName TEXT NOT NULL,
                        LookupCode TEXT NOT NULL,
                        Quantity TEXT NOT NULL,
                        UnitPrice TEXT NOT NULL,
                        DiscountAmount TEXT NOT NULL,
                        ActualAmount TEXT NOT NULL,
                        PriceSource INTEGER NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var schema = new LocalSchemaService(new LocalSqliteStore(databasePath));
            await schema.InitializeAsync();

            await using var verifyConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "PRAGMA table_info(LocalOrderLines);";
            await using var reader = await verifyCommand.ExecuteReaderAsync();

            var columns = new List<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("ItemNumber", columns);
            Assert.Contains("Kind", columns);
            Assert.Contains("ReturnSourceKey", columns);
            Assert.Contains("OriginalOrderGuid", columns);
            Assert.Contains("OriginalOrderDetailGuid", columns);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public void Payment_success_view_model_shows_receipt_total()
    {
        var order = CreateLocalOrder();
        var viewModel = new PaymentSuccessViewModel();

        viewModel.LoadFromOrder(order);

        Assert.Equal(order.ActualAmount, viewModel.TotalAmountPaid);
        Assert.Equal(order.Lines.Sum(line => line.ActualAmount), viewModel.Subtotal);
        Assert.Equal(2, viewModel.ReceiptLines.Count);
        Assert.Single(viewModel.Payments);
        Assert.Equal(order.OrderGuid, viewModel.TransactionId);
    }

    [Fact]
    public void Payment_success_view_model_builds_formatter_backed_receipt_preview()
    {
        var order = CreateLocalOrder();
        var viewModel = new PaymentSuccessViewModel();

        viewModel.LoadFromOrder(order);

        Assert.Contains(viewModel.ReceiptPreviewRows, row => row.Text.Contains("===== TAX INVOICE =====", StringComparison.Ordinal));
        // 普通单的条码预览行是 GUID 末 8 位（与小票单号同口径），不再含整串 GUID；完整 GUID 只在二维码里。
        Assert.Contains(viewModel.ReceiptPreviewRows, row => row.IsBarcode && row.Text.Contains(ReceiptOrderShortCode.Create(order.OrderGuid), StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.ReceiptPreviewRows, row => row.IsBarcode && row.Text.Contains(order.OrderGuid.ToString(), StringComparison.Ordinal));
        Assert.Equal(order.OrderGuid.ToString("D"), Assert.Single(viewModel.ReceiptPreviewRows, row => row.IsQrCode).QrCodeValue);
        Assert.Contains(viewModel.ReceiptPreviewRows, row => row.Text.Contains("GST", StringComparison.Ordinal));
    }

    [Fact]
    public void Payment_success_view_model_shows_masked_square_card_details()
    {
        var order = CreateLocalOrder(
            paymentMethod: PaymentMethodKind.Card,
            actualAmount: 9m,
            tenderedAmount: null,
            changeAmount: null,
            paymentReference: "SQ:payment-1",
            cardTransactions:
            [
                new CardTransactionDto(
                    "Square",
                    "payment-1",
                    "68aLBM",
                    "VISA",
                    null,
                    "****1111",
                    null,
                    null,
                    "COMPLETED",
                    null,
                    DateTimeOffset.UtcNow,
                    9m,
                    null)
            ]);
        var viewModel = new PaymentSuccessViewModel();

        viewModel.LoadFromOrder(order);

        Assert.True(viewModel.IsCardPaymentSummaryVisible);
        Assert.Equal("VISA | ****1111 | Auth 68aLBM | COMPLETED", viewModel.CardPaymentSummary);
        Assert.Contains(viewModel.ReceiptPreviewRows, row => row.Text.Contains("****1111", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.ReceiptPreviewRows, row => row.Text.Contains("411111", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.ReceiptPreviewRows, row => row.Text.Contains("sq-test-fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public void Payment_success_view_model_shows_cash_change_when_persisted_change_is_positive()
    {
        var order = CreateLocalOrder(tenderedAmount: 10m, changeAmount: 1m);
        var viewModel = new PaymentSuccessViewModel();

        viewModel.LoadFromOrder(order);

        Assert.True(viewModel.IsCashChangeVisible);
        Assert.Equal(10m, viewModel.TenderedAmount);
        Assert.Equal(1m, viewModel.ChangeAmount);
        Assert.Contains(viewModel.ReceiptPreviewRows, row => row.Text.Contains("Tendered", StringComparison.Ordinal));
        Assert.Contains(viewModel.ReceiptPreviewRows, row => row.Text.Contains("Change", StringComparison.Ordinal));
    }

    [Fact]
    public void Payment_success_view_model_shows_cash_change_when_persisted_change_is_zero()
    {
        var order = CreateLocalOrder(tenderedAmount: 9m, changeAmount: 0m);
        var viewModel = new PaymentSuccessViewModel();

        viewModel.LoadFromOrder(order);

        Assert.True(viewModel.IsCashChangeVisible);
        Assert.Equal(0m, viewModel.ChangeAmount);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Card, 9, 0)]
    [InlineData(PaymentMethodKind.Voucher, 9, 0)]
    [InlineData(PaymentMethodKind.Cash, -9, 0)]
    public void Payment_success_view_model_hides_cash_change_when_order_is_not_cash_sale(
        PaymentMethodKind paymentMethod,
        decimal actualAmount,
        decimal changeAmount)
    {
        var order = CreateLocalOrder(
            paymentMethod: paymentMethod,
            actualAmount: actualAmount,
            tenderedAmount: Math.Abs(actualAmount),
            changeAmount: changeAmount);
        var viewModel = new PaymentSuccessViewModel();

        viewModel.LoadFromOrder(order);

        Assert.False(viewModel.IsCashChangeVisible);
    }

    [Fact]
    public void Payment_success_view_model_hides_cash_change_for_legacy_order_without_persisted_change()
    {
        var order = CreateLocalOrder(tenderedAmount: null, changeAmount: null);
        var viewModel = new PaymentSuccessViewModel();

        viewModel.LoadFromOrder(order);

        Assert.False(viewModel.IsCashChangeVisible);
        Assert.DoesNotContain(viewModel.ReceiptPreviewRows, row => row.Text.Contains("Tendered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pos_terminal_print_last_receipt_command_invokes_callback_and_shows_status()
    {
        var printed = false;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            onOpenPayment: null,
            onPrintLastReceiptAsync: () =>
            {
                printed = true;
                return Task.FromResult(new ReceiptPrintResult(true, "Receipt printed."));
            });

        Assert.True(viewModel.PrintLastReceiptCommand.CanExecute(null));

        await viewModel.PrintLastReceiptCommand.ExecuteAsync(null);

        Assert.True(printed);
        Assert.Equal("Receipt printed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Local_price_index_search_async_matches_sync_honors_cancellation_and_stays_off_dispatcher()
    {
        const int itemCount = 10_000;
        const int measurementCount = 1;
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(Enumerable.Range(0, itemCount)
            .Select(itemIndex => CreateItem(
                $"SKU-{itemIndex:D6}",
                $"Catalog Item {itemIndex:D6}",
                $"LOOKUP-{itemIndex:D6}",
                PriceSourceKind.StoreRetailPrice,
                1m,
                storeCode: itemIndex % 2 == 0 ? "S001" : "S002")));

        var expected = index.Search("S001", "Catalog", take: 20)
            .Select(item => item.ProductCode)
            .ToArray();
        var actual = (await index.SearchAsync("S001", "Catalog", CancellationToken.None, take: 20))
            .Select(item => item.ProductCode)
            .ToArray();

        Assert.Equal(expected, actual);

        using var cancellationSource = new CancellationTokenSource();
        index.SearchProgressForTests = (query, progress) =>
        {
            if (query == "cancel" && progress == 0)
            {
                cancellationSource.Cancel();
            }
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                index.SearchAsync("S001", "cancel", cancellationSource.Token));
        }
        finally
        {
            index.SearchProgressForTests = null;
        }

        var metrics = await MeasureSearchOnWpfDispatcherAsync(index, measurementCount, "SKU-009998");

        Assert.Equal(metrics.DispatcherThreadId, metrics.InputThreadId);
        Assert.NotEqual(metrics.DispatcherThreadId, metrics.WorkerThreadId);
    }

    [Trait("Category", "Performance")]
    [ReleaseX64PerformanceFact]
    public async Task Local_price_index_search_async_340k_meets_release_performance_gate()
    {
        const int itemCount = 340_000;
        const int measurementCount = 20;
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(Enumerable.Range(0, itemCount)
            .Select(itemIndex => CreateItem(
                $"SKU-{itemIndex:D6}",
                $"Catalog Item {itemIndex:D6}",
                $"LOOKUP-{itemIndex:D6}",
                PriceSourceKind.StoreRetailPrice,
                1m,
                storeCode: itemIndex % 2 == 0 ? "S001" : "S002")));

        var metrics = await MeasureSearchOnWpfDispatcherAsync(index, measurementCount, "SKU-339998");

        Assert.True(metrics.InputLatency <= TimeSpan.FromMilliseconds(250),
            $"DispatcherPriority.Input waited {metrics.InputLatency.TotalMilliseconds:F0}ms.");
        Assert.True(metrics.NormalP95 <= TimeSpan.FromMilliseconds(750),
            $"Regular search P95 was {metrics.NormalP95.TotalMilliseconds:F0}ms.");
        Assert.True(metrics.MissP95 <= TimeSpan.FromMilliseconds(750),
            $"Miss search P95 was {metrics.MissP95.TotalMilliseconds:F0}ms.");
        Assert.True(metrics.BroadP95 <= TimeSpan.FromSeconds(2),
            $"Broad search P95 was {metrics.BroadP95.TotalMilliseconds:F0}ms.");

        Console.WriteLine(
            $"[SearchPerf] x64={Environment.Is64BitProcess} inputMs={metrics.InputLatency.TotalMilliseconds:F0} normalP95Ms={metrics.NormalP95.TotalMilliseconds:F0} missP95Ms={metrics.MissP95.TotalMilliseconds:F0} broadP95Ms={metrics.BroadP95.TotalMilliseconds:F0}");
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class ReleaseX64PerformanceFactAttribute : FactAttribute
    {
        public ReleaseX64PerformanceFactAttribute()
        {
#if DEBUG
            Skip = "性能门禁仅在 Release x64 测试进程中运行。";
#else
            if (!string.Equals(Environment.GetEnvironmentVariable("HBPOS_RUN_PERF_TESTS"), "1", StringComparison.Ordinal))
            {
                Skip = "设置 HBPOS_RUN_PERF_TESTS=1 后运行 Release x64 性能门禁。";
            }
            else if (!Environment.Is64BitProcess)
            {
                Skip = "性能门禁需要 x64 测试进程。";
            }
#endif
        }
    }

    private static async Task<(TimeSpan InputLatency, TimeSpan NormalP95, TimeSpan MissP95, TimeSpan BroadP95, int DispatcherThreadId, int InputThreadId, int WorkerThreadId)> MeasureSearchOnWpfDispatcherAsync(
        LocalSellableItemIndex index,
        int measurementCount,
        string normalQuery)
    {
        var searchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerThreadId = 0;
        index.SearchProgressForTests = (query, progress) =>
        {
            if (query == "catalog" && progress == 0)
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                searchStarted.TrySetResult();
            }
        };

        try
        {
            return await RunOnStaDispatcherAsync(async dispatcher =>
            {
                Assert.True(dispatcher.CheckAccess());
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                var dispatcherThreadId = Environment.CurrentManagedThreadId;

                var broadSearch = index.SearchAsync("S001", "catalog", CancellationToken.None, take: 20);
                await searchStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

                var inputQueuedAt = Stopwatch.GetTimestamp();
                var inputThreadId = 0;
                var inputOperation = dispatcher.InvokeAsync(
                    () => inputThreadId = Environment.CurrentManagedThreadId,
                    DispatcherPriority.Input);
                await inputOperation.Task;
                var inputLatency = Stopwatch.GetElapsedTime(inputQueuedAt);

                Assert.Equal(20, (await broadSearch).Count);
                index.SearchProgressForTests = null;

                var normalP95 = CalculateP95(await MeasureSearchSamplesAsync(index, normalQuery, measurementCount, 1));
                var missP95 = CalculateP95(await MeasureSearchSamplesAsync(index, "NO-SUCH-CATALOG", measurementCount, 0));
                var broadP95 = CalculateP95(await MeasureSearchSamplesAsync(index, "catalog", measurementCount, 20));
                return (inputLatency, normalP95, missP95, broadP95, dispatcherThreadId, inputThreadId, workerThreadId);
            });
        }
        finally
        {
            index.SearchProgressForTests = null;
        }
    }

    private static async Task<IReadOnlyList<TimeSpan>> MeasureSearchSamplesAsync(
        LocalSellableItemIndex index,
        string query,
        int measurementCount,
        int expectedResultCount)
    {
        var samples = new TimeSpan[measurementCount];
        for (var measurementIndex = 0; measurementIndex < measurementCount; measurementIndex++)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = await index.SearchAsync("S001", query, CancellationToken.None, take: 20);
            stopwatch.Stop();
            Assert.Equal(expectedResultCount, results.Count);
            samples[measurementIndex] = stopwatch.Elapsed;
        }

        return samples;
    }

    private static TimeSpan CalculateP95(IReadOnlyList<TimeSpan> samples)
    {
        Assert.NotEmpty(samples);
        var sorted = samples.OrderBy(sample => sample).ToArray();
        var percentileIndex = (int)Math.Ceiling(sorted.Length * 0.95d) - 1;
        return sorted[percentileIndex];
    }

    private static async Task<T> RunOnStaDispatcherAsync<T>(Func<Dispatcher, Task<T>> action)
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                dispatcherReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Hbpos.Client.Tests.WpfDispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = await dispatcherReady.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        try
        {
            var operation = dispatcher.InvokeAsync(() => action(dispatcher), DispatcherPriority.Normal);
            return await await operation.Task;
        }
        finally
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout), "WPF Dispatcher thread did not shut down.");
        }
    }

    private static SellableItemDto CreateItem(
        string productCode,
        string name,
        string barcode,
        PriceSourceKind priceSource,
        decimal price,
        string storeCode = "S001",
        string? itemNumber = null)
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: barcode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: barcode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static void SetUnsafeQuantity(CartLine line, decimal quantity)
    {
        var field = typeof(CartLine).GetField("_quantity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(line, quantity);
    }

    private static LocalOrder CreateLocalOrder(
        Guid? orderGuid = null,
        string deviceCode = "POS-01",
        DateTimeOffset? soldAt = null,
        string firstLookupCode = "690101",
        string firstItemNumber = "ITEM-101",
        PaymentMethodKind paymentMethod = PaymentMethodKind.Cash,
        decimal actualAmount = 9.00m,
        decimal? tenderedAmount = 10.00m,
        decimal? changeAmount = 1.00m,
        string? paymentReference = null,
        IReadOnlyList<CardTransactionDto>? cardTransactions = null)
    {
        var totalAmount = actualAmount + 0.20m;
        var firstLineAmount = decimal.Round(actualAmount - 4.00m, 2, MidpointRounding.AwayFromZero);
        var lines = new[]
        {
            new LocalOrderLine(Guid.NewGuid(), "SKU-101", null, "Organic Gala Apples", firstLookupCode, firstItemNumber, 2m, 2.50m, 0m, firstLineAmount, PriceSourceKind.StoreRetailPrice),
            new LocalOrderLine(
                Guid.NewGuid(),
                "SKU-102",
                null,
                "Whole Grain Bread",
                "690102",
                "ITEM-102",
                1m,
                4.20m,
                0.20m,
                decimal.Round(actualAmount - firstLineAmount, 2, MidpointRounding.AwayFromZero),
                PriceSourceKind.ProductBase,
                OrderLineKind.Return,
                "RETURN-SOURCE-ORDER",
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"))
        };

        return new LocalOrder(
            orderGuid ?? Guid.NewGuid(),
            "S001",
            deviceCode,
            "C001",
            "Alice",
            soldAt ?? DateTimeOffset.UtcNow,
            totalAmount,
            0.20m,
            actualAmount,
            lines,
            [new LocalPayment(Guid.NewGuid(), paymentMethod, actualAmount, paymentReference, cardTransactions)],
            tenderedAmount,
            changeAmount);
    }
}
