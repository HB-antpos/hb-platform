using System.Net;
using System.Net.Http.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Orders;
using static Hbpos.Client.Tests.SharedHeldOrderClientTestSupport;

namespace Hbpos.Client.Tests;

public sealed class OrderUploadServiceTests
{
    [Theory]
    [InlineData(HeldOrderDisposition.Unmatched, true)]
    [InlineData(HeldOrderDisposition.Primary, false)]
    [InlineData(HeldOrderDisposition.Duplicate, false)]
    public async Task Held_order_unmatched_disposition_is_reported_for_manual_reconciliation(
        HeldOrderDisposition disposition,
        bool expectsError)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-order-upload-disposition-{Guid.NewGuid():N}.db");
        var sink = new CapturingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var order = CreateLocalOrder();
            await orders.SavePendingOrderAsync(order);

            await new OrderUploadService(
                    orders,
                    new StubOrderSyncApiClient(new OrderSyncResponse(
                        order.OrderGuid,
                        Accepted: true,
                        AlreadySynced: false,
                        Message: "Synced",
                        HeldOrderDisposition: disposition)),
                    uploadRepository)
                .UploadOrderAsync(order.OrderGuid);

            // 无论 disposition 是什么，订单都必须落库——钱已经收了，
            // 不能因为挂单关联失败就把这笔销售丢掉。
            Assert.Single(await orders.GetRecentOrdersAsync());

            var unmatchedErrors = sink.Entries
                .Where(entry =>
                    string.Equals(entry.Level, "Error", StringComparison.OrdinalIgnoreCase) &&
                    entry.Message.Contains("held-order unmatched", StringComparison.Ordinal))
                .ToList();

            if (expectsError)
            {
                // Unmatched 说明订单没能关联到对应的共享挂单，可能与其他收银机重复销售，
                // 必须留下错误级别的可告警信号交人工对账。
                var error = Assert.Single(unmatchedErrors);
                Assert.Contains(order.StoreCode, error.Message, StringComparison.Ordinal);
                Assert.Contains(order.DeviceCode, error.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(unmatchedErrors);
            }
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    private sealed class CapturingApplicationLogSink : IApplicationLogSink
    {
        private readonly List<ApplicationLogEntry> _entries = [];

        public IReadOnlyList<ApplicationLogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public void Enqueue(ApplicationLogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }

    [Fact]
    public async Task ExecuteSelectedAsync_preserves_order_deduplicates_and_summarizes_failures()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var uploader = new RecordingOrderUploadService(second);
        var executor = new OrderUploadExecutionService(uploader, new StubOrderUploadRepository());

        var result = await executor.ExecuteSelectedAsync([first, second, first, Guid.Empty]);

        Assert.Equal([first, second], uploader.Attempts);
        Assert.Equal(new OrderUploadExecutionResult(2, 1, 1), result);
    }

    [Fact]
    public async Task ExecuteSelectedAsync_with_empty_selection_does_not_upload()
    {
        var uploader = new RecordingOrderUploadService(Guid.Empty);
        var executor = new OrderUploadExecutionService(uploader, new StubOrderUploadRepository());

        var result = await executor.ExecuteSelectedAsync([]);

        Assert.Empty(uploader.Attempts);
        Assert.Equal(new OrderUploadExecutionResult(0, 0, 0), result);
    }

    [Fact]
    public async Task GetReuploadableOrderGuidsAsync_forwards_the_date_range_and_terminal()
    {
        var soldFrom = DateTimeOffset.Parse("2026-07-21T00:00:00+10:00");
        var soldTo = soldFrom.AddDays(1).AddTicks(-1);
        var expected = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var repository = new StubOrderUploadRepository
        {
            ReuploadableOrderGuids = expected
        };
        var executor = new OrderUploadExecutionService(new RecordingOrderUploadService(Guid.Empty), repository);

        var actual = await executor.GetReuploadableOrderGuidsAsync(soldFrom, soldTo, "pos-01");

        Assert.Equal(expected, actual);
        Assert.NotNull(repository.LastReuploadableQuery);
        Assert.Equal(soldFrom, repository.LastReuploadableQuery.Value.SoldFrom);
        Assert.Equal(soldTo, repository.LastReuploadableQuery.Value.SoldTo);
        Assert.Equal("pos-01", repository.LastReuploadableQuery.Value.DeviceCode);
    }

    [Fact]
    public async Task Local_order_upload_repository_returns_all_reuploadable_orders_in_stable_range_order()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-reupload-query-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalOrderUploadRepository(store);
            var soldFrom = DateTimeOffset.Parse("2026-07-21T00:00:00+10:00");
            var soldTo = soldFrom.AddDays(1).AddTicks(-1);
            var eligible = new List<ReuploadableOrderSeed>
            {
                new(Guid.Parse("00000000-0000-0000-0000-000000000002"), soldFrom, "POS-01", "Synced"),
                new(Guid.Parse("00000000-0000-0000-0000-000000000001"), soldFrom, "POS-01", "Pending")
            };

            for (var index = 0; index <= 500; index++)
            {
                eligible.Add(new(
                    Guid.Parse($"00000000-0000-0000-0000-{index + 3:D12}"),
                    soldFrom.AddMinutes(index + 1),
                    "POS-01",
                    (index % 3) switch
                    {
                        0 => "Synced",
                        1 => "Pending",
                        _ => "Failed"
                    }));
            }

            eligible.Add(new(
                Guid.Parse("00000000-0000-0000-0000-999999999999"),
                soldTo,
                "POS-01",
                "Failed"));
            var excluded = new[]
            {
                new ReuploadableOrderSeed(Guid.NewGuid(), soldFrom, "POS-01", "Syncing"),
                new ReuploadableOrderSeed(Guid.NewGuid(), soldFrom, "POS-02", "Synced"),
                new ReuploadableOrderSeed(Guid.NewGuid(), soldFrom.AddTicks(-1), "POS-01", "Pending"),
                new ReuploadableOrderSeed(Guid.NewGuid(), soldTo.AddTicks(1), "POS-01", "Failed")
            };

            await schema.InitializeAsync();
            await SeedLocalOrdersAsync(store, eligible.Concat(excluded));

            var actual = await repository.GetReuploadableOrderGuidsAsync(soldFrom, soldTo, "pos-01");
            var expected = eligible
                .OrderBy(order => order.SoldAt)
                .ThenBy(order => order.OrderGuid)
                .Select(order => order.OrderGuid)
                .ToArray();

            Assert.Equal(504, actual.Count);
            Assert.Equal(expected, actual);

            var allTerminals = await repository.GetReuploadableOrderGuidsAsync(soldFrom, soldTo, null);
            Assert.Equal(505, allTerminals.Count);
            Assert.Contains(excluded[1].OrderGuid, allTerminals);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteSelectedAsync_queues_remaining_orders_until_inflight_endpoint_switch_commits()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-switch-{Guid.NewGuid():N}.db");
        const string oldAddress = "https://old.example.test/pos-api/";
        const string newAddress = "https://new.example.test/pos-api/";
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var first = CreateLocalOrder();
            var alreadySynced = CreateLocalOrder();
            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(first);
            await orders.SavePendingOrderAsync(alreadySynced);
            await uploadRepository.MarkSyncedAsync(alreadySynced.OrderGuid);

            var endpointState = new ApiRuntimeEndpointState(oldAddress);
            var handler = new EndpointSwitchOrderSyncHandler();
            using var httpClient = new HttpClient(new ApiRuntimeEndpointHandler(endpointState)
            {
                InnerHandler = handler
            })
            {
                BaseAddress = new Uri(oldAddress)
            };
            var executor = new OrderUploadExecutionService(
                new OrderUploadService(orders, new OrderSyncApiClient(httpClient), uploadRepository),
                uploadRepository);

            var execution = executor.ExecuteSelectedAsync([first.OrderGuid, alreadySynced.OrderGuid]);
            await handler.FirstRequestStarted.WaitUntilCompletedAsync();
            var transition = await endpointState.BeginTransitionAsync(newAddress, CancellationToken.None);
            // 中断后仍要回写 SQLite 状态，CI 慢盘上 2 秒预算不够，使用共享预算。
            var interrupted = await execution.WaitUntilCompletedAsync(
                () => $"requestUris=[{string.Join(", ", handler.RequestUris)}]");

            Assert.Equal(new OrderUploadExecutionResult(2, 0, 2, WasInterrupted: true), interrupted);
            Assert.Equal([new Uri($"{oldAddress}api/v1/orders/sync")], handler.RequestUris);
            Assert.All(await orders.GetRecentOrdersAsync(), item => Assert.Equal("Pending", item.SyncStatus));

            endpointState.Commit(transition);
            var resumed = await executor.ExecutePendingAsync();

            Assert.Equal(new OrderUploadExecutionResult(2, 2, 0), resumed);
            Assert.Equal(
                [
                    new Uri($"{oldAddress}api/v1/orders/sync"),
                    new Uri($"{newAddress}api/v1/orders/sync"),
                    new Uri($"{newAddress}api/v1/orders/sync")
                ],
                handler.RequestUris);
            Assert.Equal(
                "Synced",
                (await orders.GetRecentOrdersAsync())
                    .Single(item => item.OrderGuid == alreadySynced.OrderGuid)
                    .SyncStatus);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task UploadOrderAsync_keeps_order_pending_when_new_request_is_rejected_during_transition()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-transition-open-{Guid.NewGuid():N}.db");
        const string oldAddress = "https://old.example.test/pos-api/";
        const string newAddress = "https://new.example.test/pos-api/";
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var order = CreateLocalOrder();
            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var endpointState = new ApiRuntimeEndpointState(oldAddress);
            using var httpClient = new HttpClient(new ApiRuntimeEndpointHandler(endpointState)
            {
                InnerHandler = new EndpointSwitchOrderSyncHandler()
            })
            {
                BaseAddress = new Uri(oldAddress)
            };
            var uploadService = new OrderUploadService(
                orders,
                new OrderSyncApiClient(httpClient),
                uploadRepository);
            var transition = await endpointState.BeginTransitionAsync(newAddress, CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                uploadService.UploadOrderAsync(order.OrderGuid));

            Assert.Equal(
                "Pending",
                Assert.Single(await orders.GetRecentOrdersAsync()).SyncStatus);
            endpointState.Abort(transition);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteSelectedAsync_stops_batch_when_new_request_is_rejected_during_transition()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var uploader = new EndpointTransitionOrderUploadService();
        var repository = new StubOrderUploadRepository();
        var executor = new OrderUploadExecutionService(uploader, repository);

        var result = await executor.ExecuteSelectedAsync([first, second]);

        Assert.Equal(new OrderUploadExecutionResult(2, 0, 2, WasInterrupted: true), result);
        Assert.Equal([first], uploader.Attempts);
        Assert.Equal([first, second], repository.MarkedPendingOrderGuids);
    }

    [Fact]
    public async Task ExecuteOneAsync_reports_endpoint_transition_as_interrupted_not_failed()
    {
        var orderGuid = Guid.NewGuid();
        var uploader = new EndpointTransitionOrderUploadService();
        var executor = new OrderUploadExecutionService(uploader, new StubOrderUploadRepository());

        var result = await executor.ExecuteOneAsync(orderGuid);

        Assert.Equal(new OrderUploadExecutionResult(1, 0, 0, WasInterrupted: true), result);
        Assert.Equal([orderGuid], uploader.Attempts);
    }

    [Fact]
    public async Task ExecutePendingAsync_stops_after_endpoint_transition_rejection()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var uploader = new EndpointTransitionOrderUploadService();
        var repository = new StubOrderUploadRepository
        {
            PendingOrderGuids = [first, second]
        };
        var executor = new OrderUploadExecutionService(uploader, repository);

        var result = await executor.ExecutePendingAsync();

        Assert.Equal(new OrderUploadExecutionResult(2, 0, 0, WasInterrupted: true), result);
        Assert.Equal([first], uploader.Attempts);
    }

    [Fact]
    public async Task UploadOrderAsync_http_timeout_keeps_order_queued_with_error_instead_of_endpoint_switch()
    {
        await using var harness = await TimeoutUploadHarness.CreateAsync(slowOrderCount: 1, fastOrderCount: 0);
        var order = harness.SlowOrders[0];

        // 3-20：HttpClient 15 秒超时与端点切换同为 TaskCanceledException，超时不能再被当作端点切换。
        var exception = await Assert.ThrowsAsync<OrderUploadTimeoutException>(() =>
            harness.UploadService.UploadOrderAsync(order));

        Assert.IsType<TimeoutException>(exception.InnerException?.InnerException);
        var summary = await harness.GetOrderAsync(order);
        Assert.Equal("Pending", summary.SyncStatus);
        var queued = Assert.Single(await harness.SyncQueue.GetActiveItemsAsync());
        Assert.Equal("Pending", queued.Status);
        Assert.Contains("timed out", queued.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out", (await harness.SyncQueue.GetOverviewAsync()).LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteOneAsync_reports_http_timeout_as_not_completed_not_interrupted()
    {
        await using var harness = await TimeoutUploadHarness.CreateAsync(slowOrderCount: 1, fastOrderCount: 0);

        var result = await harness.Executor.ExecuteOneAsync(harness.SlowOrders[0]);

        Assert.Equal(new OrderUploadExecutionResult(1, 0, 1), result);
    }

    [Fact]
    public async Task ExecutePendingAsync_http_timeout_counts_not_completed_and_continues_with_next_order()
    {
        await using var harness = await TimeoutUploadHarness.CreateAsync(slowOrderCount: 1, fastOrderCount: 1);

        var result = await harness.Executor.ExecutePendingAsync();

        // 超时的订单不能再"占住队首"让后面的订单一笔都不试，计数也不能是"成功 0、失败 0"。
        Assert.Equal(new OrderUploadExecutionResult(2, 1, 1), result);
        Assert.Equal(2, harness.Handler.RequestCount);
        Assert.Equal("Synced", (await harness.GetOrderAsync(harness.FastOrders[0])).SyncStatus);
        Assert.Equal("Pending", (await harness.GetOrderAsync(harness.SlowOrders[0])).SyncStatus);
    }

    [Fact]
    public async Task ExecutePendingAsync_stops_round_after_two_consecutive_timeouts()
    {
        await using var harness = await TimeoutUploadHarness.CreateAsync(slowOrderCount: 3, fastOrderCount: 0);

        var result = await harness.Executor.ExecutePendingAsync();

        // 连续两笔超时说明服务端整体不可用：本轮停下，第三笔不再等满超时，留在队列等下一轮。
        Assert.Equal(new OrderUploadExecutionResult(3, 0, 3), result);
        Assert.Equal(2, harness.Handler.RequestCount);
        Assert.All(
            await harness.Orders.GetRecentOrdersAsync(),
            order => Assert.Equal("Pending", order.SyncStatus));
    }

    [Fact]
    public async Task ExecuteSelectedAsync_http_timeout_is_not_reported_as_endpoint_switch()
    {
        await using var harness = await TimeoutUploadHarness.CreateAsync(slowOrderCount: 1, fastOrderCount: 1);

        var result = await harness.Executor.ExecuteSelectedAsync([harness.SlowOrders[0], harness.FastOrders[0]]);

        Assert.Equal(new OrderUploadExecutionResult(2, 1, 1), result);
        Assert.Equal(2, harness.Handler.RequestCount);
        Assert.Equal("Synced", (await harness.GetOrderAsync(harness.FastOrders[0])).SyncStatus);
    }

    [Fact]
    public async Task ExecuteSelectedAsync_stops_after_two_consecutive_timeouts_without_touching_remaining_orders()
    {
        await using var harness = await TimeoutUploadHarness.CreateAsync(slowOrderCount: 2, fastOrderCount: 1);
        await harness.UploadRepository.MarkSyncedAsync(harness.FastOrders[0]);

        var result = await harness.Executor.ExecuteSelectedAsync(
            [harness.SlowOrders[0], harness.SlowOrders[1], harness.FastOrders[0]]);

        // 按日期重传一批 500 笔；网络整体不通时不能每笔都等满超时。未尝试的订单保持原状态。
        Assert.Equal(new OrderUploadExecutionResult(3, 0, 3), result);
        Assert.Equal(2, harness.Handler.RequestCount);
        Assert.Equal("Synced", (await harness.GetOrderAsync(harness.FastOrders[0])).SyncStatus);
    }

    [Fact]
    public async Task ExecuteSelectedAsync_stops_immediately_when_caller_cancels()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var uploader = new CallerCancellationOrderUploadService();
        var executor = new OrderUploadExecutionService(uploader, new StubOrderUploadRepository());
        using var cancellation = new CancellationTokenSource();

        var execution = executor.ExecuteSelectedAsync([first, second], cancellation.Token);
        await uploader.FirstRequestStarted.WaitUntilCompletedAsync();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            execution.WaitAsync(AsyncTestWaitSupport.DefaultTimeout));
        Assert.Equal([first], uploader.Attempts);
    }

    [Fact]
    public async Task Execution_service_serializes_cross_entry_point_uploads()
    {
        var orderGuid = Guid.NewGuid();
        var uploader = new BlockingOrderUploadService();
        var executor = new OrderUploadExecutionService(uploader, new StubOrderUploadRepository());

        var automatic = executor.ExecuteOneAsync(orderGuid);
        await uploader.FirstRequestStarted.WaitUntilCompletedAsync();
        var manual = executor.ExecuteSelectedAsync([orderGuid]);
        await Task.Yield();

        Assert.Single(uploader.Attempts);
        Assert.Equal(1, uploader.MaximumConcurrency);

        uploader.ReleaseFirstRequest();
        await Task.WhenAll(automatic, manual).WaitUntilCompletedAsync();

        Assert.Equal([orderGuid, orderGuid], uploader.Attempts);
        Assert.Equal(1, uploader.MaximumConcurrency);
    }

    [Fact]
    public async Task Order_upload_execution_service_marks_order_synced_when_api_accepts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var uploadService = new OrderUploadService(
                orders,
                new StubOrderSyncApiClient(new OrderSyncResponse(order.OrderGuid, true, false, "Synced")),
                uploadRepository);
            var executor = new OrderUploadExecutionService(uploadService, uploadRepository);

            var result = await executor.ExecutePendingAsync();
            var summary = Assert.Single(await orders.GetRecentOrdersAsync());
            var activeItems = await syncQueue.GetActiveItemsAsync();

            Assert.Equal(1, result.AttemptedCount);
            Assert.Equal(1, result.UploadedCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal("Synced", summary.SyncStatus);
            Assert.Empty(activeItems);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Order_upload_execution_service_marks_order_failed_when_api_throws()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-failed-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var uploadService = new OrderUploadService(
                orders,
                new ThrowingOrderSyncApiClient("network down"),
                uploadRepository);
            var executor = new OrderUploadExecutionService(uploadService, uploadRepository);

            var result = await executor.ExecutePendingAsync();
            var summary = Assert.Single(await orders.GetRecentOrdersAsync());
            var activeItem = Assert.Single(await syncQueue.GetActiveItemsAsync());

            Assert.Equal(1, result.AttemptedCount);
            Assert.Equal(0, result.UploadedCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal("Failed", summary.SyncStatus);
            Assert.Equal("Failed", activeItem.Status);
            Assert.Contains("network down", activeItem.ErrorMessage);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Order_upload_execution_service_execute_one_marks_order_synced_when_api_accepts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-one-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var uploadService = new OrderUploadService(
                orders,
                new StubOrderSyncApiClient(new OrderSyncResponse(order.OrderGuid, true, false, "Synced")),
                uploadRepository);
            var executor = new OrderUploadExecutionService(uploadService, uploadRepository);

            var result = await executor.ExecuteOneAsync(order.OrderGuid);
            var summary = Assert.Single(await orders.GetRecentOrdersAsync());
            var activeItems = await syncQueue.GetActiveItemsAsync();

            Assert.Equal(1, result.AttemptedCount);
            Assert.Equal(1, result.UploadedCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal("Synced", summary.SyncStatus);
            Assert.Empty(activeItems);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Order_upload_execution_service_execute_one_returns_failed_count_when_api_throws()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-one-failed-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var uploadService = new OrderUploadService(
                orders,
                new ThrowingOrderSyncApiClient("network down"),
                uploadRepository);
            var executor = new OrderUploadExecutionService(uploadService, uploadRepository);

            var result = await executor.ExecuteOneAsync(order.OrderGuid);
            var summary = Assert.Single(await orders.GetRecentOrdersAsync());
            var activeItem = Assert.Single(await syncQueue.GetActiveItemsAsync());

            Assert.Equal(1, result.AttemptedCount);
            Assert.Equal(0, result.UploadedCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal("Failed", summary.SyncStatus);
            Assert.Equal("Failed", activeItem.Status);
            Assert.Contains("network down", activeItem.ErrorMessage);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Local_order_repository_roundtrips_card_transactions()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-card-transaction-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var saved = await orders.GetOrderAsync(order.OrderGuid);

            var payment = Assert.Single(saved!.Payments);
            var transaction = Assert.Single(payment.CardTransactions!);
            Assert.Equal("ANZ", transaction.Processor);
            Assert.Equal("TXN-1", transaction.TxnRef);
            Assert.Equal("****1234", transaction.MaskedCardNumber);
            Assert.Equal("merchant receipt", transaction.ReceiptText);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task UploadOrderAsync_sends_card_transactions_to_sync_api()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-card-upload-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var order = CreateLocalOrder();
            var apiClient = new CapturingOrderSyncApiClient(order.OrderGuid);

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var uploadService = new OrderUploadService(orders, apiClient, uploadRepository);
            await uploadService.UploadOrderAsync(order.OrderGuid);

            var payment = Assert.Single(apiClient.LastRequest!.Payments);
            var transaction = Assert.Single(payment.CardTransactions!);
            Assert.Equal("ANZ", transaction.Processor);
            Assert.Equal("TXN-1", transaction.TxnRef);
            Assert.Equal("merchant receipt", transaction.ReceiptText);
            Assert.Equal(OrderLineKind.Return, apiClient.LastRequest.Lines[1].Kind);
            Assert.Equal("RETURN-UPLOAD-1", apiClient.LastRequest.Lines[1].ReturnSourceKey);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task UploadOrderAsync_carries_held_order_source_and_accepts_all_dispositions()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-held-source-upload-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var heldRepository = new SharedHeldOrderRepository(
                store,
                new SharedHeldOrderClientTestSupport.TestPayloadProtector(),
                new SharedHeldOrderClientTestSupport.TestPayloadSerializer());

            await schema.InitializeAsync();

            // RemoteClaim（Active）来源订单：上传请求必须携带 HoldGuid+ClaimGuid+RemoteClaim。
            var remoteHold = Guid.NewGuid();
            var remoteClaim = Guid.NewGuid();
            var remoteOrder = CreateLocalOrder() with { DeviceCode = "POS-01" };
            await SeedActiveRemoteClaimAsync(
                heldRepository, remoteClaim, remoteHold, "POS-01", "prepare-upload-1", "activate-upload-1");
            await orders.SavePendingOrderWithHeldSourceAsync(
                remoteOrder,
                new LocalHeldOrderCompletionContext(
                    remoteHold,
                    remoteClaim,
                    SharedHeldOrderClaimSource.RemoteClaim,
                    "prepare-upload-1",
                    "activate-upload-1",
                    BoundOrderGuid: null,
                    "2026-07-28T00:05:00.000Z"));

            var capturingClient = new CapturingOrderSyncApiClient(remoteOrder.OrderGuid);
            var uploadService = new OrderUploadService(orders, capturingClient, uploadRepository);
            await uploadService.UploadOrderAsync(remoteOrder.OrderGuid);

            Assert.NotNull(capturingClient.LastRequest!.HeldOrderSource);
            Assert.Equal(remoteHold, capturingClient.LastRequest.HeldOrderSource.HoldGuid);
            Assert.Equal(remoteClaim, capturingClient.LastRequest.HeldOrderSource.ClaimGuid);
            Assert.Equal(
                HeldOrderSourceKind.RemoteClaim,
                capturingClient.LastRequest.HeldOrderSource.Kind);

            // OfflineOrigin 来源订单：上传请求携带 HoldGuid 且 ClaimGuid=null。
            var offlineHold = Guid.NewGuid();
            var offlineClaim = Guid.NewGuid();
            var offlineOrder = CreateLocalOrder() with { DeviceCode = "POS-02" };
            await SeedActiveRemoteClaimAsync(
                heldRepository, offlineClaim, offlineHold, "POS-02", "prepare-upload-2", "activate-upload-2",
                source: SharedHeldOrderClaimSource.OfflineOrigin);
            await orders.SavePendingOrderWithHeldSourceAsync(
                offlineOrder,
                new LocalHeldOrderCompletionContext(
                    offlineHold,
                    offlineClaim,
                    SharedHeldOrderClaimSource.OfflineOrigin,
                    "prepare-upload-2",
                    "activate-upload-2",
                    BoundOrderGuid: null,
                    "2026-07-28T00:05:00.000Z"));

            var offlineCapturing = new CapturingOrderSyncApiClient(offlineOrder.OrderGuid);
            await new OrderUploadService(orders, offlineCapturing, uploadRepository)
                .UploadOrderAsync(offlineOrder.OrderGuid);
            Assert.NotNull(offlineCapturing.LastRequest!.HeldOrderSource);
            Assert.Equal(offlineHold, offlineCapturing.LastRequest.HeldOrderSource.HoldGuid);
            Assert.Null(offlineCapturing.LastRequest.HeldOrderSource.ClaimGuid);
            Assert.Equal(
                HeldOrderSourceKind.OfflineOrigin,
                offlineCapturing.LastRequest.HeldOrderSource.Kind);

            // 服务端 disposition（Primary/Duplicate/Unmatched）只用于记录/调和：全部 Accepted。
            foreach (var disposition in new[]
                     {
                         HeldOrderDisposition.Primary,
                         HeldOrderDisposition.Duplicate,
                         HeldOrderDisposition.Unmatched
                     })
            {
                var hold = Guid.NewGuid();
                var claim = Guid.NewGuid();
                var order = CreateLocalOrder() with { DeviceCode = $"POS-{10 + (int)disposition}" };
                var deviceCode = $"POS-{10 + (int)disposition}";
                await SeedActiveRemoteClaimAsync(
                    heldRepository, claim, hold, deviceCode, $"prepare-{disposition}", $"activate-{disposition}");
                await orders.SavePendingOrderWithHeldSourceAsync(
                    order,
                    new LocalHeldOrderCompletionContext(
                        hold,
                        claim,
                        SharedHeldOrderClaimSource.RemoteClaim,
                        $"prepare-{disposition}",
                        $"activate-{disposition}",
                        BoundOrderGuid: null,
                        "2026-07-28T00:05:00.000Z"));
                var service = new OrderUploadService(
                    orders,
                    new StubOrderSyncApiClient(new OrderSyncResponse(
                        order.OrderGuid,
                        Accepted: true,
                        AlreadySynced: false,
                        Message: disposition.ToString(),
                        HeldOrderDisposition: disposition)),
                    uploadRepository);
                await service.UploadOrderAsync(order.OrderGuid);
            }
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Order_upload_execution_service_keeps_order_pending_when_cashier_authorization_is_missing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-auth-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var order = CreateLocalOrder();

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var uploadService = new OrderUploadService(
                orders,
                new UnauthorizedOrderSyncApiClient(),
                uploadRepository);
            var executor = new OrderUploadExecutionService(uploadService, uploadRepository);

            var result = await executor.ExecutePendingAsync();
            var summary = Assert.Single(await orders.GetRecentOrdersAsync());
            var savedOrder = await orders.GetOrderAsync(order.OrderGuid);
            var activeItem = Assert.Single(await syncQueue.GetActiveItemsAsync());

            Assert.Equal(1, result.AttemptedCount);
            Assert.Equal(0, result.UploadedCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal("Pending", summary.SyncStatus);
            Assert.Equal("Pending", activeItem.Status);
            Assert.Equal(order.CashierId, savedOrder!.CashierId);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteSelectedAsync_counts_missing_authorization_as_failed_and_keeps_order_pending()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-selected-auth-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var order = CreateLocalOrder();
            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);
            var executor = new OrderUploadExecutionService(
                new OrderUploadService(orders, new UnauthorizedOrderSyncApiClient(), uploadRepository),
                uploadRepository);

            var result = await executor.ExecuteSelectedAsync([order.OrderGuid]);

            Assert.Equal(new OrderUploadExecutionResult(1, 0, 1), result);
            Assert.Equal("Pending", Assert.Single(await orders.GetRecentOrdersAsync()).SyncStatus);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteSelectedAsync_keeps_already_synced_order_synced()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-already-synced-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var order = CreateLocalOrder();
            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);
            await uploadRepository.MarkSyncedAsync(order.OrderGuid);
            var executor = new OrderUploadExecutionService(
                new OrderUploadService(
                    orders,
                    new StubOrderSyncApiClient(new OrderSyncResponse(order.OrderGuid, true, true, "AlreadySynced")),
                    uploadRepository),
                uploadRepository);

            var result = await executor.ExecuteSelectedAsync([order.OrderGuid]);

            Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), result);
            Assert.Equal("Synced", Assert.Single(await orders.GetRecentOrdersAsync()).SyncStatus);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task UploadOrderAsync_ignores_voucher_balance_suffix_when_mapping_payment()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-voucher-upload-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var order = CreateLocalOrder() with
            {
                Payments =
                [
                    new LocalPayment(
                        Guid.NewGuid(),
                        PaymentMethodKind.Voucher,
                        5.00m,
                        "VOUCHER:ABC123:token-1:12.34")
                ]
            };
            var apiClient = new CapturingOrderSyncApiClient(order.OrderGuid);

            await schema.InitializeAsync();
            await orders.SavePendingOrderAsync(order);

            var uploadService = new OrderUploadService(orders, apiClient, uploadRepository);
            await uploadService.UploadOrderAsync(order.OrderGuid);

            var payment = Assert.Single(apiClient.LastRequest!.Payments);
            Assert.Equal(PaymentMethodKind.Voucher, payment.Method);
            Assert.Equal("ABC123", payment.Reference);
            Assert.Equal("token-1", payment.ReservationToken);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_skips_failed_order_during_backoff_and_picks_it_again_once_due()
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var order = await harness.SaveOrderAsync();
        harness.Api.Failure = _ => new HttpRequestException("network down");

        Assert.Equal(new OrderUploadExecutionResult(1, 0, 1), await harness.Executor.ExecutePendingAsync());
        var failed = await harness.ReadQueueRowAsync(order.OrderGuid);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(30), failed.Delay);

        // 退避期内的 15 秒定时上传不再原样重发失败单。
        harness.Api.Failure = _ => null;
        Assert.Equal(new OrderUploadExecutionResult(0, 0, 0), await harness.Executor.ExecutePendingAsync());
        Assert.Single(harness.Api.RequestedOrderGuids);

        await harness.ExpireBackoffAsync();
        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecutePendingAsync());
        var synced = await harness.ReadQueueRowAsync(order.OrderGuid);
        Assert.Equal("Synced", synced.Status);
        Assert.Equal(0, synced.AttemptCount);
        Assert.Null(synced.NextAttemptAt);
    }

    [Fact]
    public async Task ExecutePendingAsync_twenty_failed_orders_no_longer_block_a_new_order()
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var failedOrders = new List<Guid>();
        for (var index = 0; index < 20; index++)
        {
            failedOrders.Add((await harness.SaveOrderAsync()).OrderGuid);
        }

        harness.Api.Failure = _ => new HttpRequestException("network down");
        Assert.Equal(new OrderUploadExecutionResult(20, 0, 20), await harness.Executor.ExecutePendingAsync());

        // 旧实现按 CreatedAt 取前 20 张 Pending/Failed：20 张失败单永远占满一批，新单再也轮不到。
        var newOrder = await harness.SaveOrderAsync();
        harness.Api.Failure = _ => null;
        harness.Api.ClearRequests();

        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecutePendingAsync());
        Assert.Equal([newOrder.OrderGuid], harness.Api.RequestedOrderGuids);
        Assert.Equal("Synced", await harness.GetSyncStatusAsync(newOrder.OrderGuid));
        foreach (var orderGuid in failedOrders)
        {
            Assert.Equal("Failed", await harness.GetSyncStatusAsync(orderGuid));
        }
    }

    [Theory]
    [InlineData("network", null, "30,60,120,300,300")]
    [InlineData("500", null, "30,60,120,300,300")]
    [InlineData("404", null, "30,60,120,300,300")]
    [InlineData("408", null, "30,60,120,300,300")]
    [InlineData("429", null, "30,60,120,300,300")]
    [InlineData("403", "DEVICE_SCOPE_FORBIDDEN", "30,60,120,300,300")]
    [InlineData("400", "ORDER_SYNC_INVALID", "30,60,120,300,600,1200,1800,1800")]
    [InlineData("409", null, "30,60,120,300,600,1200,1800,1800")]
    [InlineData("not-accepted", null, "30,60,120,300,600,1200,1800,1800")]
    public async Task Failed_upload_backoff_caps_at_five_minutes_unless_server_reliably_rejects_the_order(
        string failure,
        string? errorCode,
        string expectedDelaySeconds)
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var order = await harness.SaveOrderAsync();
        if (failure == "not-accepted")
        {
            harness.Api.NotAccepted = true;
        }
        else
        {
            harness.Api.Failure = _ => failure == "network"
                ? new HttpRequestException("network down")
                : new CatalogApiException($"HTTP {failure}", (HttpStatusCode)int.Parse(failure), errorCode);
        }

        var expected = expectedDelaySeconds.Split(',').Select(value => TimeSpan.FromSeconds(int.Parse(value))).ToArray();
        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            await harness.ExpireBackoffAsync();
            Assert.Equal(new OrderUploadExecutionResult(1, 0, 1), await harness.Executor.ExecutePendingAsync());
            var row = await harness.ReadQueueRowAsync(order.OrderGuid);
            Assert.Equal("Failed", row.Status);
            Assert.Equal(attempt, row.AttemptCount);
            Assert.Equal(expected[attempt - 1], row.Delay);
        }
    }

    [Fact]
    public async Task Upload_timeout_counts_a_transient_attempt_but_keeps_order_pending()
    {
        await using var harness = await TimeoutUploadHarness.CreateAsync(slowOrderCount: 1, fastOrderCount: 0);
        var order = harness.SlowOrders[0];

        Assert.Equal(new OrderUploadExecutionResult(1, 0, 1), await harness.Executor.ExecutePendingAsync());
        Assert.Equal("Pending", (await harness.GetOrderAsync(order)).SyncStatus);
        var row = await ReadQueueRowAsync(harness.Store, order);
        Assert.Equal("Pending", row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(30), row.Delay);

        // 退避期内不再每 15 秒等满一次超时。
        Assert.Equal(new OrderUploadExecutionResult(0, 0, 0), await harness.Executor.ExecutePendingAsync());
        Assert.Equal(1, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task Endpoint_transition_requeue_clears_backoff_without_counting_an_attempt()
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var order = await harness.SaveOrderAsync();
        harness.Api.Failure = _ => new HttpRequestException("network down");
        await harness.Executor.ExecutePendingAsync();

        await harness.UploadRepository.MarkPendingAsync(order.OrderGuid);

        var requeued = await harness.ReadQueueRowAsync(order.OrderGuid);
        Assert.Equal("Pending", requeued.Status);
        Assert.Equal(1, requeued.AttemptCount);
        Assert.Null(requeued.NextAttemptAt);
        Assert.Equal([order.OrderGuid], await harness.UploadRepository.GetPendingOrderGuidsAsync());
    }

    [Fact]
    public async Task Manual_single_and_selected_retry_ignore_backoff()
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var first = await harness.SaveOrderAsync();
        var second = await harness.SaveOrderAsync();
        harness.Api.Failure = _ => new HttpRequestException("network down");
        Assert.Equal(new OrderUploadExecutionResult(2, 0, 2), await harness.Executor.ExecutePendingAsync());
        harness.Api.Failure = _ => null;

        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecuteOneAsync(first.OrderGuid));
        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecuteSelectedAsync([second.OrderGuid]));
        Assert.Equal("Synced", await harness.GetSyncStatusAsync(first.OrderGuid));
        Assert.Equal("Synced", await harness.GetSyncStatusAsync(second.OrderGuid));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "DEVICE_AUTH_REQUIRED")]
    [InlineData(HttpStatusCode.Forbidden, null)]
    public async Task Global_authorization_failure_ends_the_round_without_counting_attempts_or_moving_backoff(
        HttpStatusCode statusCode,
        string? errorCode)
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var backedOff = await harness.SaveOrderAsync();
        var fresh = await harness.SaveOrderAsync();
        var untouched = await harness.SaveOrderAsync();
        harness.Api.Failure = request => request.OrderGuid == backedOff.OrderGuid
            ? new HttpRequestException("network down")
            : null;
        await harness.Executor.ExecuteSelectedAsync([backedOff.OrderGuid]);
        var beforeAuthorization = await harness.ReadQueueRowAsync(backedOff.OrderGuid);
        harness.Api.Failure = _ => new CatalogApiException("authorization required", statusCode, errorCode);
        harness.Api.ClearRequests();

        // 自动上传：收银员票据/设备授权是全局条件，第一笔被拒就结束本轮，后面的订单不再逐笔请求。
        Assert.Equal(new OrderUploadExecutionResult(2, 0, 0), await harness.Executor.ExecutePendingAsync());
        Assert.Equal([fresh.OrderGuid], harness.Api.RequestedOrderGuids);

        // 手动重试也一样；已在退避中的订单被授权拦下时，失败次数与下次时间都保持原样。
        harness.Api.ClearRequests();
        Assert.Equal(
            new OrderUploadExecutionResult(3, 0, 3),
            await harness.Executor.ExecuteSelectedAsync([backedOff.OrderGuid, fresh.OrderGuid, untouched.OrderGuid]));
        Assert.Equal([backedOff.OrderGuid], harness.Api.RequestedOrderGuids);
        var afterAuthorization = await harness.ReadQueueRowAsync(backedOff.OrderGuid);
        Assert.Equal("Pending", afterAuthorization.Status);
        Assert.Equal(beforeAuthorization.AttemptCount, afterAuthorization.AttemptCount);
        Assert.Equal(beforeAuthorization.NextAttemptAt, afterAuthorization.NextAttemptAt);
        foreach (var orderGuid in new[] { fresh.OrderGuid, untouched.OrderGuid })
        {
            var row = await harness.ReadQueueRowAsync(orderGuid);
            Assert.Equal("Pending", row.Status);
            Assert.Equal(0, row.AttemptCount);
            Assert.Null(row.NextAttemptAt);
        }
    }

    [Fact]
    public async Task Device_scope_forbidden_is_a_per_order_failure_and_the_round_continues()
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var rebound = await harness.SaveOrderAsync();
        var current = await harness.SaveOrderAsync();
        harness.Api.Failure = request => request.OrderGuid == rebound.OrderGuid
            ? new CatalogApiException("Device is not authorized for this store.", HttpStatusCode.Forbidden, "DEVICE_SCOPE_FORBIDDEN")
            : null;

        Assert.Equal(new OrderUploadExecutionResult(2, 1, 1), await harness.Executor.ExecutePendingAsync());

        // 设备换绑前的旧单与当前授权范围不符：只是这一笔失败并退避，不能当成全局授权失效把整轮停下。
        var row = await harness.ReadQueueRowAsync(rebound.OrderGuid);
        Assert.Equal("Failed", row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal("Synced", await harness.GetSyncStatusAsync(current.OrderGuid));
    }

    [Fact]
    public async Task Order_with_unissued_refund_voucher_is_held_until_the_reference_is_written_back()
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var refundPayment = new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, -3.00m, " voucher_refund_pending\t");
        var held = await harness.SaveOrderAsync(
        [
            new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, 12.00m, null),
            refundPayment
        ]);
        var normal = await harness.SaveOrderAsync();

        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecutePendingAsync());
        Assert.Equal([normal.OrderGuid], harness.Api.RequestedOrderGuids);

        // 直接按订单号上传的入口（同步中心选中重试、交易历史重传）同样不能把占位引用传上去，也不改上传状态。
        Assert.Equal(new OrderUploadExecutionResult(1, 0, 1), await harness.Executor.ExecuteSelectedAsync([held.OrderGuid]));
        Assert.Equal([normal.OrderGuid], harness.Api.RequestedOrderGuids);
        var row = await harness.ReadQueueRowAsync(held.OrderGuid);
        Assert.Equal("Pending", row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.NextAttemptAt);

        // 签券成功回写真实券号后，下一轮自动上传正常带出券号。
        await harness.Orders.UpdatePaymentReferenceAsync(refundPayment.PaymentGuid, "VOUCHER_REFUND:RF-0001");
        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecutePendingAsync());
        var uploaded = harness.Api.Requests.Single(request => request.OrderGuid == held.OrderGuid);
        Assert.Contains(uploaded.Payments, payment => payment.Method == PaymentMethodKind.Voucher && payment.Reference == "RF-0001");
        Assert.DoesNotContain(
            harness.Api.Requests.SelectMany(request => request.Payments),
            payment => (payment.Reference ?? string.Empty).Contains("PENDING", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Synced", await harness.GetSyncStatusAsync(held.OrderGuid));
    }

    [Fact]
    public async Task Refund_voucher_hold_past_threshold_shows_reason_in_sync_center_without_touching_status_or_backoff()
    {
        // 卡退款经恢复服务落单的订单从不签券（签券失败后离开付款页同理）：除了中心日志，同步中心也要看得到暂扣原因，
        // 不能只是一条没有原因的 Pending。原因只写 ErrorMessage，状态、失败次数、退避时间一概不动。
        await using var harness = await BackoffHarness.CreateAsync();
        static LocalPayment PendingRefundVoucher() =>
            new(Guid.NewGuid(), PaymentMethodKind.Voucher, -3.00m, "VOUCHER_REFUND_PENDING");
        var stuckRefund = PendingRefundVoucher();
        var stuck = await harness.SaveOrderAsync(
        [
            new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, 12.00m, null),
            stuckRefund
        ]);
        var failedBeforeUpgrade = await harness.SaveOrderAsync([PendingRefundVoucher()]);
        var justSaved = await harness.SaveOrderAsync([PendingRefundVoucher()]);
        var syncedWithPlaceholder = await harness.SaveOrderAsync([PendingRefundVoucher()]);

        // 入队 20 分钟仍是占位引用（已过 10 分钟告警阈值、仍在 30 分钟暂扣窗口内）。
        // 用 +14:00 的本地时间串：按字符串比较会显得比现在还晚，只有按 julianday 换算才判得出已超时。
        var queuedAt = DateTimeOffset.UtcNow.AddMinutes(-20).ToOffset(TimeSpan.FromHours(14))
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await harness.ExecuteSqlAsync(
            $"UPDATE SyncQueue SET CreatedAt = '{queuedAt}' WHERE EntityId IN " +
            $"('{stuck.OrderGuid}', '{failedBeforeUpgrade.OrderGuid}', '{syncedWithPlaceholder.OrderGuid}');");
        // 升级前就已失败过的旧单：带着旧错误、失败次数与未到期的退避时间。
        const string farFutureDue = "2099-01-01T00:00:00.0000000+00:00";
        await harness.ExecuteSqlAsync(
            $"UPDATE SyncQueue SET Status = 'Failed', AttemptCount = 2, NextAttemptAt = '{farFutureDue}', " +
            $"ErrorMessage = 'old error' WHERE EntityId = '{failedBeforeUpgrade.OrderGuid}';");
        // 本 PR 之前已带着占位引用上传成功的历史订单：已同步的行不能再被标成暂扣。
        await harness.ExecuteSqlAsync($"UPDATE SyncQueue SET Status = 'Synced' WHERE EntityId = '{syncedWithPlaceholder.OrderGuid}';");

        Assert.Equal(new OrderUploadExecutionResult(0, 0, 0), await harness.Executor.ExecutePendingAsync());
        Assert.Empty(harness.Api.Requests);

        var stuckRow = await harness.ReadQueueRowAsync(stuck.OrderGuid);
        Assert.StartsWith("Refund voucher not issued", stuckRow.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("Pending", stuckRow.Status);
        Assert.Equal(0, stuckRow.AttemptCount);
        Assert.Null(stuckRow.NextAttemptAt);
        Assert.Null(stuckRow.LastTriedAt);

        var failedRow = await harness.ReadQueueRowAsync(failedBeforeUpgrade.OrderGuid);
        Assert.Equal(stuckRow.ErrorMessage, failedRow.ErrorMessage);
        Assert.Equal("Failed", failedRow.Status);
        Assert.Equal(2, failedRow.AttemptCount);
        Assert.Equal(farFutureDue, failedRow.NextAttemptAt);

        // 刚落库、签券通常几秒内就会回写的订单不提前打上暂扣原因；已同步的行不受影响。
        Assert.Null((await harness.ReadQueueRowAsync(justSaved.OrderGuid)).ErrorMessage);
        Assert.Null((await harness.ReadQueueRowAsync(syncedWithPlaceholder.OrderGuid)).ErrorMessage);
        Assert.Contains(
            await harness.SyncQueue.GetActiveItemsAsync(),
            item => item.EntityId == stuck.OrderGuid &&
                item.Status == "Pending" &&
                (item.ErrorMessage ?? string.Empty).StartsWith("Refund voucher not issued", StringComparison.Ordinal));

        // 补签回写真实券号后：下一轮自动上传正常带出券号，暂扣原因随之清掉。
        await harness.Orders.UpdatePaymentReferenceAsync(stuckRefund.PaymentGuid, "VOUCHER_REFUND:RF-0002");
        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecutePendingAsync());
        Assert.Equal([stuck.OrderGuid], harness.Api.RequestedOrderGuids);
        var uploadedRow = await harness.ReadQueueRowAsync(stuck.OrderGuid);
        Assert.Equal("Synced", uploadedRow.Status);
        Assert.Null(uploadedRow.ErrorMessage);
    }

    [Fact]
    public async Task Unissued_refund_voucher_hold_expires_after_thirty_minutes_on_both_queue_and_direct_upload_paths()
    {
        // 暂扣只挡"落库 → 签券回写"之间的插队；从不签券的订单（卡退款经恢复服务落单等）不能永远留在本机，
        // 入队满 30 分钟后自动上传与按订单号直接上传都要放行，且用同一把尺子（队列入队时间）。
        await using var harness = await BackoffHarness.CreateAsync();
        static LocalPayment PendingRefundVoucher() =>
            new(Guid.NewGuid(), PaymentMethodKind.Voucher, -3.00m, "VOUCHER_REFUND_PENDING");
        var expiredQueued = await harness.SaveOrderAsync([PendingRefundVoucher()]);
        var expiredSelected = await harness.SaveOrderAsync([PendingRefundVoucher()]);
        var fresh = await harness.SaveOrderAsync([PendingRefundVoucher()]);
        // +14:00 的本地时间串：按字符串比较会显得比现在还晚，只有按 julianday 换算才判得出已过窗口。
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-31).ToOffset(TimeSpan.FromHours(14))
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await harness.ExecuteSqlAsync(
            $"UPDATE SyncQueue SET CreatedAt = '{expiredAt}' WHERE EntityId IN " +
            $"('{expiredQueued.OrderGuid}', '{expiredSelected.OrderGuid}');");

        // 按订单号直接上传：过了窗口照常上传；仍在窗口内的照旧暂扣、不改状态。
        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecuteSelectedAsync([expiredSelected.OrderGuid]));
        Assert.Equal(new OrderUploadExecutionResult(1, 0, 1), await harness.Executor.ExecuteSelectedAsync([fresh.OrderGuid]));
        Assert.Equal([expiredSelected.OrderGuid], harness.Api.RequestedOrderGuids);

        // 后台自动上传：只放行过了窗口的那笔，窗口内的继续暂扣。
        Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await harness.Executor.ExecutePendingAsync());
        Assert.Equal([expiredSelected.OrderGuid, expiredQueued.OrderGuid], harness.Api.RequestedOrderGuids);
        Assert.Equal("Synced", await harness.GetSyncStatusAsync(expiredQueued.OrderGuid));
        Assert.Equal("Synced", await harness.GetSyncStatusAsync(expiredSelected.OrderGuid));
        var freshRow = await harness.ReadQueueRowAsync(fresh.OrderGuid);
        Assert.Equal("Pending", freshRow.Status);
        Assert.Equal(0, freshRow.AttemptCount);
        Assert.Null(freshRow.NextAttemptAt);

        // "全部重试"同样只放行过了窗口的订单：此时没有可传的了。
        Assert.Equal(new OrderUploadExecutionResult(0, 0, 0), await harness.Executor.ExecuteAllRetryableAsync());
    }

    [Fact]
    public async Task Reupload_of_order_whose_synced_queue_row_was_cleaned_up_recreates_the_queue_row()
    {
        await using var harness = await BackoffHarness.CreateAsync();
        var order = await harness.SaveOrderAsync();
        await harness.UploadRepository.MarkSyncedAsync(order.OrderGuid);
        // 模拟启动清理删掉 30 天前已同步的队列行。
        await harness.ExecuteSqlAsync($"DELETE FROM SyncQueue WHERE EntityId = '{order.OrderGuid}';");
        harness.Api.Failure = _ => new HttpRequestException("network down");

        // 交易历史重传失败：队列行要补回来，同步中心看得到、后台按退避继续重试，而不是只剩 LocalOrders.SyncStatus=Failed。
        Assert.Equal(new OrderUploadExecutionResult(1, 0, 1), await harness.Executor.ExecuteSelectedAsync([order.OrderGuid]));
        var row = await harness.ReadQueueRowAsync(order.OrderGuid);
        Assert.Equal("Failed", row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Contains(
            await harness.SyncQueue.GetActiveItemsAsync(),
            item => item.EntityId == order.OrderGuid && item.Status == "Failed");

        // 本地不存在的订单不会凭空补出队列行。
        await harness.UploadRepository.MarkFailedAsync(Guid.NewGuid(), "missing");
        Assert.Equal(1L, await harness.ScalarAsync("SELECT COUNT(*) FROM SyncQueue;"));
    }

    private static async Task<QueueRow> ReadQueueRowAsync(LocalSqliteStore store, Guid orderGuid)
    {
        await using var connection = await store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Status, AttemptCount, NextAttemptAt, LastTriedAt, ErrorMessage
            FROM SyncQueue
            WHERE EntityId = $OrderGuid AND EntityType = 'Order';
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"SyncQueue row missing for {orderGuid:D}");
        return new QueueRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private sealed record QueueRow(
        string Status,
        int AttemptCount,
        string? NextAttemptAt,
        string? LastTriedAt,
        string? ErrorMessage)
    {
        // NextAttemptAt（UTC）与 LastTriedAt（本地带偏移）由同一时刻写出，二者之差就是这次的退避延时。
        public TimeSpan? Delay => NextAttemptAt is null || LastTriedAt is null
            ? null
            : DateTimeOffset.Parse(NextAttemptAt, System.Globalization.CultureInfo.InvariantCulture) -
              DateTimeOffset.Parse(LastTriedAt, System.Globalization.CultureInfo.InvariantCulture);
    }

    // 真实 SQLite 仓储 + 真实上传服务 + 可编程的服务端响应；退避"到期"通过把 NextAttemptAt 改到过去模拟。
    private sealed class BackoffHarness : IAsyncDisposable
    {
        private readonly string _databasePath;

        private BackoffHarness(string databasePath, LocalSqliteStore store)
        {
            _databasePath = databasePath;
            Store = store;
            Orders = new LocalOrderRepository(store);
            UploadRepository = new LocalOrderUploadRepository(store);
            SyncQueue = new SyncQueueRepository(store);
            Executor = new OrderUploadExecutionService(
                new OrderUploadService(Orders, Api, UploadRepository),
                UploadRepository);
        }

        public LocalSqliteStore Store { get; }

        public LocalOrderRepository Orders { get; }

        public LocalOrderUploadRepository UploadRepository { get; }

        public SyncQueueRepository SyncQueue { get; }

        public ProgrammableOrderSyncApiClient Api { get; } = new();

        public OrderUploadExecutionService Executor { get; }

        public static async Task<BackoffHarness> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-backoff-{Guid.NewGuid():N}.db");
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            return new BackoffHarness(databasePath, store);
        }

        public async Task<LocalOrder> SaveOrderAsync(IReadOnlyList<LocalPayment>? payments = null)
        {
            var order = CreateLocalOrder();
            if (payments is not null)
            {
                order = order with { Payments = payments };
            }

            await Orders.SavePendingOrderAsync(order);
            return order;
        }

        public Task<QueueRow> ReadQueueRowAsync(Guid orderGuid) => OrderUploadServiceTests.ReadQueueRowAsync(Store, orderGuid);

        public async Task<string> GetSyncStatusAsync(Guid orderGuid) =>
            (await Orders.GetRecentOrdersAsync()).Single(order => order.OrderGuid == orderGuid).SyncStatus;

        public Task ExpireBackoffAsync() => ExecuteSqlAsync(
            "UPDATE SyncQueue SET NextAttemptAt = '2000-01-01T00:00:00.0000000+00:00' WHERE NextAttemptAt IS NOT NULL;");

        public async Task ExecuteSqlAsync(string sql)
        {
            await using var connection = await Store.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = await Store.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(_databasePath);
        }
    }

    private sealed class ProgrammableOrderSyncApiClient : IOrderSyncApiClient
    {
        private readonly List<OrderSyncRequest> _requests = [];

        public Func<OrderSyncRequest, Exception?> Failure { get; set; } = _ => null;

        public bool NotAccepted { get; set; }

        public IReadOnlyList<OrderSyncRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public IReadOnlyList<Guid> RequestedOrderGuids => Requests.Select(request => request.OrderGuid).ToArray();

        public void ClearRequests()
        {
            lock (_requests)
            {
                _requests.Clear();
            }
        }

        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            var failure = Failure(request);
            if (failure is not null)
            {
                return Task.FromException<OrderSyncResponse>(failure);
            }

            return Task.FromResult(NotAccepted
                ? new OrderSyncResponse(request.OrderGuid, false, false, "Rejected by test")
                : new OrderSyncResponse(request.OrderGuid, true, false, "Synced"));
        }
    }

    private static LocalOrder CreateLocalOrder()
    {
        return new LocalOrder(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.UtcNow,
            9.20m,
            0.20m,
            9.00m,
            [
                new LocalOrderLine(Guid.NewGuid(), "SKU-101", null, "Organic Gala Apples", "690101", "ITEM-101", 2m, 2.50m, 0m, 5.00m, PriceSourceKind.StoreRetailPrice),
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
                    4.00m,
                    PriceSourceKind.ProductBase,
                    OrderLineKind.Return,
                    "RETURN-UPLOAD-1",
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Guid.Parse("22222222-2222-2222-2222-222222222222"))
            ],
            [
                new LocalPayment(
                    Guid.NewGuid(),
                    PaymentMethodKind.Card,
                    9.00m,
                    "ANZ:TXN-1",
                    [
                        new CardTransactionDto(
                            "ANZ",
                            "TXN-1",
                            "123456",
                            "VISA",
                            4,
                            "****1234",
                            "MID-1",
                            "00",
                            "APPROVED",
                            "42",
                            DateTimeOffset.Parse("2026-05-26T00:00:00Z"),
                            9.00m,
                            "merchant receipt")
                    ])
            ]);
    }

    private static async Task SeedActiveRemoteClaimAsync(
        SharedHeldOrderRepository repository,
        Guid claimId,
        Guid holdGuid,
        string deviceCode,
        string prepareKey,
        string activateKey,
        SharedHeldOrderClaimSource source = SharedHeldOrderClaimSource.RemoteClaim)
    {
        Assert.True(await repository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
            claimId,
            holdGuid,
            "S001",
            deviceCode,
            source,
            prepareKey,
            SampleCanonical(),
            "2026-07-28T00:00:02.000Z",
            "2026-07-28T00:05:00.000Z")));
        Assert.True(await repository.TryActivateClaimAsync(
            claimId, prepareKey, activateKey, serverRevision: 7, "2026-07-28T00:00:03.000Z"));
    }

    private static async Task SeedLocalOrdersAsync(
        LocalSqliteStore store,
        IEnumerable<ReuploadableOrderSeed> orders)
    {
        await using var connection = await store.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LocalOrders (
                OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt,
                TotalAmount, DiscountAmount, ActualAmount, SyncStatus)
            VALUES (
                $OrderGuid, 'S001', $DeviceCode, 'C001', 'Alice', $SoldAt,
                '1.00', '0.00', '1.00', $SyncStatus);
            """;
        var orderGuid = command.CreateParameter();
        orderGuid.ParameterName = "$OrderGuid";
        command.Parameters.Add(orderGuid);
        var deviceCode = command.CreateParameter();
        deviceCode.ParameterName = "$DeviceCode";
        command.Parameters.Add(deviceCode);
        var soldAt = command.CreateParameter();
        soldAt.ParameterName = "$SoldAt";
        command.Parameters.Add(soldAt);
        var syncStatus = command.CreateParameter();
        syncStatus.ParameterName = "$SyncStatus";
        command.Parameters.Add(syncStatus);

        foreach (var order in orders)
        {
            orderGuid.Value = order.OrderGuid.ToString("D");
            deviceCode.Value = order.DeviceCode;
            soldAt.Value = order.SoldAt.ToString("O");
            syncStatus.Value = order.SyncStatus;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private sealed record ReuploadableOrderSeed(
        Guid OrderGuid,
        DateTimeOffset SoldAt,
        string DeviceCode,
        string SyncStatus);

    private sealed class StubOrderSyncApiClient(OrderSyncResponse response) : IOrderSyncApiClient
    {
        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingOrderSyncApiClient(Guid orderGuid) : IOrderSyncApiClient
    {
        public OrderSyncRequest? LastRequest { get; private set; }

        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new OrderSyncResponse(orderGuid, true, false, "Synced"));
        }
    }

    private sealed class ThrowingOrderSyncApiClient(string message) : IOrderSyncApiClient
    {
        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class UnauthorizedOrderSyncApiClient : IOrderSyncApiClient
    {
        public Task<OrderSyncResponse> SyncAsync(
            OrderSyncRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new CatalogApiException("forbidden", HttpStatusCode.Forbidden);
        }
    }

    private sealed class RecordingOrderUploadService(Guid failingOrder) : IOrderUploadService
    {
        public List<Guid> Attempts { get; } = [];

        public Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            Attempts.Add(orderGuid);
            return orderGuid == failingOrder
                ? Task.FromException(new InvalidOperationException("failed"))
                : Task.CompletedTask;
        }
    }

    private sealed class CallerCancellationOrderUploadService : IOrderUploadService
    {
        private readonly TaskCompletionSource _firstRequestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstRequestStarted => _firstRequestStarted.Task;

        public List<Guid> Attempts { get; } = [];

        public async Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            Attempts.Add(orderGuid);
            _firstRequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class EndpointTransitionOrderUploadService : IOrderUploadService
    {
        public List<Guid> Attempts { get; } = [];

        public Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            Attempts.Add(orderGuid);
            return Task.FromCanceled(new CancellationToken(canceled: true));
        }
    }

    private sealed class BlockingOrderUploadService : IOrderUploadService
    {
        private readonly TaskCompletionSource _firstRequestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeRequests;

        public Task FirstRequestStarted => _firstRequestStarted.Task;

        public List<Guid> Attempts { get; } = [];

        public int MaximumConcurrency { get; private set; }

        public void ReleaseFirstRequest() => _releaseFirstRequest.TrySetResult();

        public async Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            Attempts.Add(orderGuid);
            var active = Interlocked.Increment(ref _activeRequests);
            MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            try
            {
                if (Attempts.Count == 1)
                {
                    _firstRequestStarted.TrySetResult();
                    await _releaseFirstRequest.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }
    }

    // 真实 HttpClient（经过与生产一致的 ApiRuntimeEndpointHandler）+ 真实上传服务与 SQLite 仓储。
    // "慢单"让服务端一直不响应，由 HttpClient 自身的超时打断，得到与生产完全相同的异常形状。
    private sealed class TimeoutUploadHarness : IAsyncDisposable
    {
        private const string ApiAddress = "https://api.example.test/pos-api/";
        private readonly string _databasePath;
        private readonly HttpClient _httpClient;

        private TimeoutUploadHarness(
            string databasePath,
            LocalSqliteStore store,
            SlowOrderSyncHandler handler,
            IReadOnlyList<Guid> slowOrders,
            IReadOnlyList<Guid> fastOrders)
        {
            _databasePath = databasePath;
            Store = store;
            Handler = handler;
            SlowOrders = slowOrders;
            FastOrders = fastOrders;
            Orders = new LocalOrderRepository(store);
            UploadRepository = new LocalOrderUploadRepository(store);
            SyncQueue = new SyncQueueRepository(store);
            _httpClient = new HttpClient(new ApiRuntimeEndpointHandler(new ApiRuntimeEndpointState(ApiAddress))
            {
                InnerHandler = handler
            })
            {
                BaseAddress = new Uri(ApiAddress),
                // 生产是 15 秒；这里缩短但留足余量，避免慢 CI 上正常请求也被判超时。
                Timeout = TimeSpan.FromSeconds(1)
            };
            UploadService = new OrderUploadService(Orders, new OrderSyncApiClient(_httpClient), UploadRepository);
            Executor = new OrderUploadExecutionService(UploadService, UploadRepository);
        }

        public LocalSqliteStore Store { get; }

        public SlowOrderSyncHandler Handler { get; }

        public IReadOnlyList<Guid> SlowOrders { get; }

        public IReadOnlyList<Guid> FastOrders { get; }

        public LocalOrderRepository Orders { get; }

        public LocalOrderUploadRepository UploadRepository { get; }

        public SyncQueueRepository SyncQueue { get; }

        public OrderUploadService UploadService { get; }

        public OrderUploadExecutionService Executor { get; }

        public static async Task<TimeoutUploadHarness> CreateAsync(int slowOrderCount, int fastOrderCount)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-timeout-{Guid.NewGuid():N}.db");
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var orders = new LocalOrderRepository(store);
            var slowOrders = new List<Guid>();
            var fastOrders = new List<Guid>();
            // 先存慢单再存快单，上传队列按创建时间排序，慢单排在前面。
            for (var index = 0; index < slowOrderCount + fastOrderCount; index++)
            {
                var order = CreateLocalOrder();
                await orders.SavePendingOrderAsync(order);
                (index < slowOrderCount ? slowOrders : fastOrders).Add(order.OrderGuid);
            }

            return new TimeoutUploadHarness(
                databasePath,
                store,
                new SlowOrderSyncHandler(slowOrders),
                slowOrders,
                fastOrders);
        }

        public async Task<LocalOrderSummary> GetOrderAsync(Guid orderGuid) =>
            (await Orders.GetRecentOrdersAsync()).Single(order => order.OrderGuid == orderGuid);

        public async ValueTask DisposeAsync()
        {
            _httpClient.Dispose();
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(_databasePath);
        }
    }

    private sealed class SlowOrderSyncHandler(IReadOnlyCollection<Guid> slowOrders) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var syncRequest = await request.Content!.ReadFromJsonAsync<OrderSyncRequest>(
                cancellationToken: cancellationToken);
            if (slowOrders.Contains(syncRequest!.OrderGuid))
            {
                // 服务端迟迟不响应，直到 HttpClient 的超时取消本次请求。
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ApiResult<OrderSyncResponse>.Ok(
                    new OrderSyncResponse(syncRequest.OrderGuid, true, false, "Synced")))
            };
        }
    }

    private sealed class EndpointSwitchOrderSyncHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstRequestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task FirstRequestStarted => _firstRequestStarted.Task;

        public List<Uri> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var syncRequest = await request.Content!.ReadFromJsonAsync<OrderSyncRequest>(
                cancellationToken: cancellationToken);
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                _firstRequestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ApiResult<OrderSyncResponse>.Ok(
                    new OrderSyncResponse(syncRequest!.OrderGuid, true, true, "AlreadySynced")))
            };
        }
    }

    private sealed class StubOrderUploadRepository : ILocalOrderUploadRepository
    {
        public IReadOnlyList<Guid> PendingOrderGuids { get; init; } = [];

        public IReadOnlyList<Guid> ReuploadableOrderGuids { get; init; } = [];

        public List<Guid> MarkedPendingOrderGuids { get; } = [];

        public (DateTimeOffset SoldFrom, DateTimeOffset SoldTo, string? DeviceCode)? LastReuploadableQuery { get; private set; }

        public Task<IReadOnlyList<Guid>> GetPendingOrderGuidsAsync(int take = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(PendingOrderGuids.Take(take).ToArray());

        public Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
            DateTimeOffset soldFrom,
            DateTimeOffset soldTo,
            string? deviceCode,
            CancellationToken cancellationToken = default)
        {
            LastReuploadableQuery = (soldFrom, soldTo, deviceCode);
            return Task.FromResult(ReuploadableOrderGuids);
        }

        public Task MarkSyncingAsync(Guid orderGuid, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkPendingAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            MarkedPendingOrderGuids.Add(orderGuid);
            return Task.CompletedTask;
        }
        public Task MarkPendingAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default) =>
            MarkPendingAsync(orderGuid, cancellationToken);
        public Task MarkSyncedAsync(Guid orderGuid, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkFailedAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
