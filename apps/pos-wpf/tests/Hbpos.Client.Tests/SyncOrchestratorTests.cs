using System.Collections.Concurrent;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

public sealed class SyncOrchestratorTests
{
    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(2, 0, 2)]
    [InlineData(3, 1, 0)]
    public async Task Retry_reports_endpoint_switch_interruption_with_all_unfinished_uploads(
        int attempted,
        int uploaded,
        int failed)
    {
        var pending = CreateItem("Pending");
        var executor = new CapturingOrderExecutor(
            new OrderUploadExecutionResult(attempted, uploaded, failed, WasInterrupted: true));
        var status = string.Empty;
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, null),
                [pending])),
            executor,
            new LocalizationService(),
            setStatusMessage: value => status = value);
        await orchestrator.RefreshPendingSyncAsync();

        orchestrator.SelectAllSyncOrdersCommand.Execute(null);
        await orchestrator.RetrySelectedSyncOrdersCommand.ExecuteAsync(null);

        // 端点切换中断时，批量上传可能没把未尝试的订单计入失败；提示要说明是切换暂停，未完成数按"尝试减成功"算。
        Assert.Equal(
            $"Upload paused while the server address was switching: {uploaded} succeeded, {attempted - uploaded} not completed. The rest will continue after the switch.",
            status);
    }

    [Fact]
    public async Task Select_all_orders_selects_only_retryable_items_and_selected_retry_keeps_partial_failure_summary()
    {
        var pending = CreateItem("Pending");
        var failed = CreateItem("Failed");
        var syncing = CreateItem("Syncing");
        var initialSnapshot = new ShellSyncCenterSnapshot(
            new SyncQueueOverview(1, 1, 1, "network"),
            [pending, failed, syncing]);
        var refreshedFailed = new SyncQueueListItem(
            failed.EntityId,
            "Order",
            "Failed",
            failed.CreatedAt,
            DateTimeOffset.UtcNow,
            "still failed",
            failed.Amount);
        var refreshedSnapshot = new ShellSyncCenterSnapshot(
            new SyncQueueOverview(0, 1, 0, "still failed"),
            [refreshedFailed]);
        var snapshotService = new SequenceSyncCenterService(initialSnapshot, refreshedSnapshot);
        var executor = new CapturingOrderExecutor(new OrderUploadExecutionResult(2, 1, 1));
        var status = string.Empty;
        var orchestrator = new SyncOrchestrator(
            snapshotService,
            executor,
            new LocalizationService(),
            setStatusMessage: value => status = value);

        await orchestrator.RefreshPendingSyncAsync();

        orchestrator.SelectAllSyncOrdersCommand.Execute(null);

        Assert.True(pending.Selection.IsSelected);
        Assert.True(failed.Selection.IsSelected);
        Assert.False(syncing.Selection.IsSelected);

        await orchestrator.RetrySelectedSyncOrdersCommand.ExecuteAsync(null);

        Assert.Equal([pending.EntityId, failed.EntityId], executor.SelectedIds);
        Assert.Equal(2, snapshotService.CallCount);
        var refreshed = Assert.Single(orchestrator.SyncCenterOrders);
        Assert.Same(refreshedFailed, refreshed);
        Assert.Equal("Failed", refreshed.Status);
        Assert.False(refreshed.Selection.IsSelected);
        Assert.Equal("Upload retry completed: 1 succeeded, 1 not completed.", status);
    }

    [Fact]
    public async Task Refresh_combines_order_and_settlement_uploads_without_using_a_settlement_amount()
    {
        var order = CreateItem("Pending");
        var settlementPending = CreateSettlementItem(LocalLinklySettlementUploadStatus.Pending);
        var settlementFailed = CreateSettlementItem(LocalLinklySettlementUploadStatus.Rejected);
        var settlementUploading = CreateSettlementItem(LocalLinklySettlementUploadStatus.Uploading);
        var pendingCount = -1;
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, "order network error"),
                [order])),
            NoopOrderUploadExecutionService.Instance,
            new LocalizationService(),
            onPendingSyncCountChanged: value => pendingCount = value,
            linklySettlementUploadQueueReader: new StaticSettlementQueueReader(
                new LinklySettlementUploadOverview(1, 1, 1, "settlement rejected"),
                [settlementPending, settlementFailed, settlementUploading]),
            linklySettlementUploadExecutionService: new CapturingSettlementExecutor(
                new LinklySettlementUploadExecutionResult(0, 0, 0, 0, false)));

        await orchestrator.RefreshPendingSyncAsync();

        Assert.Equal(2, orchestrator.PendingUploadCount);
        Assert.Equal(1, orchestrator.FailedUploadCount);
        Assert.Equal(1, orchestrator.SyncingOrderCount);
        Assert.Equal(2, pendingCount);
        Assert.Same(order, orchestrator.SyncCenterOrders[0]);
        Assert.Equal(4, orchestrator.SyncCenterOrders.Count);
        var settlementItems = orchestrator.SyncCenterOrders
            .Where(item => item.EntityType == "LinklySettlement")
            .ToArray();
        Assert.Equal(["Pending", "Failed", "Syncing"], settlementItems.Select(item => item.Status));
        Assert.All(settlementItems, item => Assert.Equal("-", item.AmountDisplay));
        Assert.True(settlementItems[0].CanRetry);
        Assert.True(settlementItems[1].CanRetry);
        Assert.False(settlementItems[2].CanRetry);
        Assert.Contains("CloudBackendAsync | Failed | NotSubmitted", settlementItems[0].ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("settlement rejected", settlementItems[1].ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("order network error", orchestrator.LastOrderSyncErrorText, StringComparison.Ordinal);
        Assert.Contains("Linkly Settlement: settlement rejected", orchestrator.LastOrderSyncErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settlement_refresh_failure_keeps_order_snapshot_available()
    {
        var order = CreateItem("Pending");
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, null),
                [order])),
            NoopOrderUploadExecutionService.Instance,
            new LocalizationService(),
            linklySettlementUploadQueueReader: new ThrowingSettlementQueueReader("database locked"));

        await orchestrator.RefreshPendingSyncAsync();

        Assert.Same(order, Assert.Single(orchestrator.SyncCenterOrders));
        Assert.Equal(1, orchestrator.PendingUploadCount);
        Assert.Equal("Linkly settlement refresh failed: database locked", orchestrator.LastOrderSyncErrorText);
    }

    [Fact]
    public async Task Selected_retry_routes_orders_and_settlements_to_their_own_execution_services()
    {
        var order = CreateItem("Pending");
        var settlement = CreateSettlementItem(LocalLinklySettlementUploadStatus.Rejected);
        var orderExecutor = new CapturingOrderExecutor(new OrderUploadExecutionResult(1, 1, 0));
        var settlementExecutor = new CapturingSettlementExecutor(
            new LinklySettlementUploadExecutionResult(1, 1, 0, 0, false));
        var status = string.Empty;
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, null),
                [order])),
            orderExecutor,
            new LocalizationService(),
            setStatusMessage: value => status = value,
            linklySettlementUploadQueueReader: new StaticSettlementQueueReader(
                new LinklySettlementUploadOverview(0, 1, 0, null),
                [settlement]),
            linklySettlementUploadExecutionService: settlementExecutor);
        await orchestrator.RefreshPendingSyncAsync();
        orchestrator.SelectAllSyncOrdersCommand.Execute(null);

        await orchestrator.RetrySelectedSyncOrdersCommand.ExecuteAsync(null);

        Assert.Equal([order.EntityId], orderExecutor.SelectedIds);
        Assert.Equal([settlement.SettlementGuid], settlementExecutor.OneIds);
        Assert.Equal("Upload retry completed: 2 succeeded, 0 not completed.", status);
    }

    [Fact]
    public async Task Single_settlement_retry_does_not_call_the_order_executor()
    {
        var settlement = CreateSettlementItem(LocalLinklySettlementUploadStatus.Pending);
        var orderExecutor = new CapturingOrderExecutor(new OrderUploadExecutionResult(1, 1, 0));
        var settlementExecutor = new CapturingSettlementExecutor(
            new LinklySettlementUploadExecutionResult(1, 1, 0, 0, false));
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(0, 0, 0, null),
                [])),
            orderExecutor,
            new LocalizationService(),
            linklySettlementUploadQueueReader: new StaticSettlementQueueReader(
                new LinklySettlementUploadOverview(1, 0, 0, null),
                [settlement]),
            linklySettlementUploadExecutionService: settlementExecutor);
        await orchestrator.RefreshPendingSyncAsync();
        var item = Assert.Single(orchestrator.SyncCenterOrders);

        await orchestrator.RetrySyncOrderCommand.ExecuteAsync(item);

        Assert.Equal([settlement.SettlementGuid], settlementExecutor.OneIds);
        Assert.Empty(orderExecutor.OneIds);
    }

    [Fact]
    public async Task Retry_all_executes_order_queue_and_every_settlement_beyond_the_visible_page()
    {
        var orderExecutor = new CapturingOrderExecutor(new OrderUploadExecutionResult(1, 1, 0));
        var settlementExecutor = new CapturingSettlementExecutor(
            new LinklySettlementUploadExecutionResult(1, 1, 0, 0, false));
        var settlements = Enumerable.Range(0, 21)
            .Select(_ => CreateSettlementItem(LocalLinklySettlementUploadStatus.Pending))
            .ToArray();
        var queueReader = new StaticSettlementQueueReader(
            new LinklySettlementUploadOverview(21, 0, 0, null),
            settlements);
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, null),
                [CreateItem("Pending")])),
            orderExecutor,
            new LocalizationService(),
            linklySettlementUploadQueueReader: queueReader,
            linklySettlementUploadExecutionService: settlementExecutor);
        await orchestrator.RefreshPendingSyncAsync();

        await orchestrator.RetryAllSyncOrdersCommand.ExecuteAsync(null);

        Assert.Equal(1, orderExecutor.PendingCallCount);
        Assert.Equal(0, settlementExecutor.PendingCallCount);
        Assert.Equal(21, settlementExecutor.OneIds.Count);
        Assert.Contains(int.MaxValue, queueReader.RequestedTakes);
    }

    [Fact]
    public async Task Auto_retry_executes_both_order_and_settlement_queues()
    {
        var orderExecutor = new CapturingOrderExecutor(new OrderUploadExecutionResult(1, 1, 0));
        var settlementExecutor = new CapturingSettlementExecutor(
            new LinklySettlementUploadExecutionResult(1, 1, 0, 0, false));
        var orchestrator = CreateCombinedRetryOrchestrator(orderExecutor, settlementExecutor);

        await orchestrator.TryAutoRetryPendingAsync(CancellationToken.None);

        Assert.Equal(1, orderExecutor.PendingCallCount);
        Assert.Equal(1, settlementExecutor.PendingCallCount);
    }

    [Fact]
    public async Task Deferred_settlement_retry_is_reported_as_not_completed()
    {
        var settlement = CreateSettlementItem(LocalLinklySettlementUploadStatus.Pending);
        var status = string.Empty;
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(0, 0, 0, null),
                [])),
            NoopOrderUploadExecutionService.Instance,
            new LocalizationService(),
            setStatusMessage: value => status = value,
            linklySettlementUploadQueueReader: new StaticSettlementQueueReader(
                new LinklySettlementUploadOverview(1, 0, 0, null),
                [settlement]),
            linklySettlementUploadExecutionService: new CapturingSettlementExecutor(
                new LinklySettlementUploadExecutionResult(1, 0, 0, 1, false)));
        await orchestrator.RefreshPendingSyncAsync();

        await orchestrator.RetrySyncOrderCommand.ExecuteAsync(Assert.Single(orchestrator.SyncCenterOrders));

        Assert.Equal("Upload retry completed: 0 succeeded, 1 not completed.", status);
    }

    [Fact]
    public async Task Audit_retry_refreshes_selected_rejected_item_as_pending()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-sync-center-audit-{Guid.NewGuid():N}.db");
        var store = new ClientLogOutboxStore(databasePath);
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var authorization = new DeviceAuthorizationState();
        authorization.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW-01", "secret"));
        using var client = new HttpClient { BaseAddress = new Uri("https://api.example.test/") };
        using var uploader = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(false),
            authorization);
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            await store.EnqueueAsync(
                ClientLogOutboxKind.OperationAudit,
                eventId,
                now,
                "{\"storeCode\":\"S001\",\"deviceCode\":\"POS-01\"}",
                now,
                CancellationToken.None);
            await store.ApplyResultsAsync(
                ClientLogOutboxKind.OperationAudit,
                [],
                [new ClientLogRejection(eventId, "REJECTED", "old error")],
                now,
                CancellationToken.None);
            var orchestrator = new SyncOrchestrator(
                new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                    new SyncQueueOverview(0, 0, 0, null), [])),
                NoopOrderUploadExecutionService.Instance,
                new LocalizationService(),
                logOutboxStore: store,
                operationAuditUploadService: uploader,
                deviceAuthorizationState: authorization);

            await orchestrator.RefreshPendingSyncAsync();
            Assert.Equal("Rejected", Assert.Single(orchestrator.SyncCenterAuditLogs).Status);
            orchestrator.SyncCenterAuditLogs[0].Selection.IsSelected = true;

            await orchestrator.RetrySelectedAuditLogsCommand.ExecuteAsync(null);

            var refreshed = Assert.Single(orchestrator.SyncCenterAuditLogs);
            Assert.Equal("Pending", refreshed.Status);
            Assert.Null(refreshed.ErrorCode);
            Assert.Null(refreshed.ErrorMessage);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(databasePath + suffix))
                {
                    File.Delete(databasePath + suffix);
                }
            }
        }
    }

    [Fact]
    public async Task Empty_order_selection_does_not_execute_or_report_completion()
    {
        var pending = CreateItem("Pending");
        var executor = new CapturingOrderExecutor(new OrderUploadExecutionResult(1, 1, 0));
        var status = string.Empty;
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, null),
                [pending])),
            executor,
            new LocalizationService(),
            setStatusMessage: value => status = value);
        await orchestrator.RefreshPendingSyncAsync();

        await orchestrator.RetrySelectedSyncOrdersCommand.ExecuteAsync(null);

        Assert.Equal(0, executor.SelectedCallCount);
        Assert.Equal(string.Empty, status);
    }

    [Fact]
    public async Task Empty_audit_selection_does_not_report_queued_retry()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-sync-center-empty-audit-{Guid.NewGuid():N}.db");
        var store = new ClientLogOutboxStore(databasePath);
        var authorization = new DeviceAuthorizationState();
        authorization.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW-01", "secret"));
        var status = string.Empty;
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            var orchestrator = new SyncOrchestrator(
                new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                    new SyncQueueOverview(0, 0, 0, null), [])),
                NoopOrderUploadExecutionService.Instance,
                new LocalizationService(),
                setStatusMessage: value => status = value,
                logOutboxStore: store,
                deviceAuthorizationState: authorization);
            await orchestrator.RefreshPendingSyncAsync();

            await orchestrator.RetrySelectedAuditLogsCommand.ExecuteAsync(null);

            Assert.Equal(string.Empty, status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(databasePath + suffix))
                {
                    File.Delete(databasePath + suffix);
                }
            }
        }
    }

    [Fact]
    public async Task Audit_read_failure_keeps_order_snapshot_available()
    {
        var blockedParent = Path.GetTempFileName();
        try
        {
            var pending = CreateItem("Pending");
            var authorization = new DeviceAuthorizationState();
            authorization.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW-01", "secret"));
            var orchestrator = new SyncOrchestrator(
                new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                    new SyncQueueOverview(1, 0, 0, null),
                    [pending])),
                NoopOrderUploadExecutionService.Instance,
                new LocalizationService(),
                logOutboxStore: new ClientLogOutboxStore(Path.Combine(blockedParent, "logs.db")),
                deviceAuthorizationState: authorization);

            await orchestrator.RefreshPendingSyncAsync();

            Assert.Same(pending, Assert.Single(orchestrator.SyncCenterOrders));
            Assert.StartsWith("Audit log refresh failed:", orchestrator.LastAuditSyncErrorText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(blockedParent);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_list_write_back_failure_is_reported_as_audit_error_without_failing_refresh(
        bool failInCollectionChanged)
    {
        var pending = CreateItem("Pending");
        var notified = new List<string>();
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, null),
                [pending])),
            NoopOrderUploadExecutionService.Instance,
            new LocalizationService(),
            notifyPropertyChanged: name =>
            {
                notified.Add(name);
                // 模拟 MainViewModel.PropertyChanged 的某个订阅方在审计列表通知时抛错。
                if (!failInCollectionChanged && name == nameof(SyncOrchestrator.SyncCenterAuditLogs))
                {
                    throw new InvalidOperationException("audit subscriber failed");
                }
            });
        if (failInCollectionChanged)
        {
            // 模拟审计列表 CollectionChanged 的订阅方抛错。
            orchestrator.SyncCenterAuditLogs.CollectionChanged += (_, _) =>
                throw new InvalidOperationException("audit subscriber failed");
        }

        // 写回界面阶段的异常与读取失败一样只落到审计错误文案，不能让整个同步中心刷新失败：
        // 收款完成后续步骤、自动重试、手动重试都依赖这次刷新正常结束。
        await orchestrator.RefreshPendingSyncAsync();

        Assert.Same(pending, Assert.Single(orchestrator.SyncCenterOrders));
        Assert.Equal(1, orchestrator.PendingUploadCount);
        Assert.StartsWith("Audit log refresh failed:", orchestrator.LastAuditSyncErrorText, StringComparison.Ordinal);
        Assert.Contains("audit subscriber failed", orchestrator.LastAuditSyncErrorText, StringComparison.Ordinal);
        Assert.Equal(nameof(SyncOrchestrator.LastAuditSyncErrorText), notified[^1]);
    }

    [Fact]
    public async Task Refresh_reads_local_queues_off_the_caller_context_and_applies_results_on_it()
    {
        var order = CreateItem("Pending");
        var settlement = CreateSettlementItem(LocalLinklySettlementUploadStatus.Pending);
        var syncCenter = new ThreadRecordingSyncCenterService(new ShellSyncCenterSnapshot(
            new SyncQueueOverview(1, 0, 0, null),
            [order]));
        var settlementReader = new StaticSettlementQueueReader(
            new LinklySettlementUploadOverview(1, 0, 0, null),
            [settlement]);
        var callbackThreadIds = new List<int>();
        var orchestrator = new SyncOrchestrator(
            syncCenter,
            NoopOrderUploadExecutionService.Instance,
            new LocalizationService(),
            onPendingSyncCountChanged: _ => callbackThreadIds.Add(Environment.CurrentManagedThreadId),
            notifyPropertyChanged: _ => callbackThreadIds.Add(Environment.CurrentManagedThreadId),
            linklySettlementUploadQueueReader: settlementReader);

        var uiThreadId = await SingleThreadSynchronizationContextTestSupport.RunAsync(
            () => orchestrator.RefreshPendingSyncAsync());

        // 15 秒定时刷新从 Dispatcher 进来：订单快照与结算队列这类本地读取不能在 UI 线程（及其同步上下文）上执行。
        var snapshotRead = Assert.Single(syncCenter.Calls);
        Assert.NotEqual(uiThreadId, snapshotRead.ThreadId);
        Assert.False(snapshotRead.OnUiContext);
        Assert.NotEmpty(settlementReader.Calls);
        Assert.All(settlementReader.Calls, call =>
        {
            Assert.NotEqual(uiThreadId, call.ThreadId);
            Assert.False(call.OnUiContext);
        });

        // 读取结果仍回到调用方线程写回绑定属性与回调。
        Assert.NotEmpty(callbackThreadIds);
        Assert.All(callbackThreadIds, threadId => Assert.Equal(uiThreadId, threadId));
        Assert.Same(order, orchestrator.SyncCenterOrders[0]);
        Assert.Equal(2, orchestrator.SyncCenterOrders.Count);
        Assert.Equal(2, orchestrator.PendingUploadCount);
    }

    [Fact]
    public async Task Auto_retry_runs_uploads_off_the_caller_context_and_keeps_state_updates_on_it()
    {
        var orderExecutor = new CapturingOrderExecutor(new OrderUploadExecutionResult(1, 1, 0));
        var settlementExecutor = new CapturingSettlementExecutor(
            new LinklySettlementUploadExecutionResult(1, 1, 0, 0, false));
        var callbackThreadIds = new List<int>();
        var orchestrator = new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, null),
                [CreateItem("Pending")])),
            orderExecutor,
            new LocalizationService(),
            onPendingSyncCountChanged: _ => callbackThreadIds.Add(Environment.CurrentManagedThreadId),
            notifyPropertyChanged: _ => callbackThreadIds.Add(Environment.CurrentManagedThreadId),
            linklySettlementUploadQueueReader: new StaticSettlementQueueReader(
                new LinklySettlementUploadOverview(1, 0, 0, null),
                [CreateSettlementItem(LocalLinklySettlementUploadStatus.Pending)]),
            linklySettlementUploadExecutionService: settlementExecutor);
        orchestrator.RetryAllSyncOrdersCommand.CanExecuteChanged +=
            (_, _) => callbackThreadIds.Add(Environment.CurrentManagedThreadId);

        var uiThreadId = await SingleThreadSynchronizationContextTestSupport.RunAsync(
            () => orchestrator.TryAutoRetryPendingAsync(CancellationToken.None));

        // 自动重试的上传执行（读待传队列、HTTP、回写上传状态）整体不在 UI 线程上跑。
        var orderUpload = Assert.Single(orderExecutor.PendingCalls);
        Assert.NotEqual(uiThreadId, orderUpload.ThreadId);
        Assert.False(orderUpload.OnUiContext);
        var settlementUpload = Assert.Single(settlementExecutor.PendingCalls);
        Assert.NotEqual(uiThreadId, settlementUpload.ThreadId);
        Assert.False(settlementUpload.OnUiContext);

        // 命令可执行状态、绑定属性通知仍全部在调用方线程。
        Assert.NotEmpty(callbackThreadIds);
        Assert.All(callbackThreadIds, threadId => Assert.Equal(uiThreadId, threadId));
    }

    [Fact]
    public async Task Earlier_refresh_returning_last_does_not_overwrite_newer_sync_center_snapshot()
    {
        var staleItem = CreateItem("Failed");
        var freshItem = CreateItem("Pending");
        var syncCenter = new FirstCallGatedSyncCenterService(
            new ShellSyncCenterSnapshot(new SyncQueueOverview(0, 1, 0, "stale error"), [staleItem]),
            new ShellSyncCenterSnapshot(new SyncQueueOverview(1, 0, 0, null), [freshItem]));
        var orchestrator = new SyncOrchestrator(
            syncCenter,
            NoopOrderUploadExecutionService.Instance,
            new LocalizationService());

        // 先发起的刷新卡在读取上（例如 SQLite 等锁），随后发起的刷新先完成并写回界面。
        var earlierRefresh = orchestrator.RefreshPendingSyncAsync();
        await WaitUntilAsync(() => syncCenter.CallCount == 1);
        await orchestrator.RefreshPendingSyncAsync();
        Assert.Same(freshItem, Assert.Single(orchestrator.SyncCenterOrders));

        syncCenter.ReleaseFirstCall();
        await earlierRefresh.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        // 读取挪到线程池后会出现乱序返回：旧读取不能把较新的快照盖回去。
        Assert.Same(freshItem, Assert.Single(orchestrator.SyncCenterOrders));
        Assert.Equal(1, orchestrator.PendingUploadCount);
        Assert.Equal(0, orchestrator.FailedUploadCount);
    }

    private static SyncQueueListItem CreateItem(string status) => new(
        Guid.NewGuid(),
        "Order",
        status,
        DateTimeOffset.UtcNow,
        null,
        null,
        1m);

    private static LinklySettlementUploadQueueItem CreateSettlementItem(
        LocalLinklySettlementUploadStatus status) => new(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            DateTime.Today,
            status,
            DateTimeOffset.UtcNow,
            1,
            0,
            1,
            null,
            DateTimeOffset.UtcNow,
            status == LocalLinklySettlementUploadStatus.Rejected ? "REJECTED" : null,
            status == LocalLinklySettlementUploadStatus.Rejected ? "settlement rejected" : null,
            null,
            "CloudBackendAsync",
            LocalLinklySettlementStatus.Failed,
            ProviderSubmissionState.NotSubmitted);

    private static SyncOrchestrator CreateCombinedRetryOrchestrator(
        CapturingOrderExecutor orderExecutor,
        CapturingSettlementExecutor settlementExecutor)
    {
        var order = CreateItem("Pending");
        var settlement = CreateSettlementItem(LocalLinklySettlementUploadStatus.Pending);
        return new SyncOrchestrator(
            new StaticSyncCenterService(new ShellSyncCenterSnapshot(
                new SyncQueueOverview(1, 0, 0, null),
                [order])),
            orderExecutor,
            new LocalizationService(),
            linklySettlementUploadQueueReader: new StaticSettlementQueueReader(
                new LinklySettlementUploadOverview(1, 0, 0, null),
                [settlement]),
            linklySettlementUploadExecutionService: settlementExecutor);
    }

    private sealed class StaticSyncCenterService(ShellSyncCenterSnapshot snapshot) : IShellSyncCenterService
    {
        public Task<ShellSyncCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class SequenceSyncCenterService(params ShellSyncCenterSnapshot[] snapshots) : IShellSyncCenterService
    {
        public int CallCount { get; private set; }

        public Task<ShellSyncCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(CallCount, snapshots.Length - 1);
            CallCount++;
            return Task.FromResult(snapshots[index]);
        }
    }

    // 记录仓储/执行服务被调用时所在的线程，以及是否处在测试用"UI"同步上下文里。
    private sealed record ThreadObservation(int ThreadId, bool OnUiContext)
    {
        public static ThreadObservation Capture() => new(
            Environment.CurrentManagedThreadId,
            SingleThreadSynchronizationContextTestSupport.IsOnUiContext);
    }

    private sealed class ThreadRecordingSyncCenterService(ShellSyncCenterSnapshot snapshot) : IShellSyncCenterService
    {
        private readonly ConcurrentQueue<ThreadObservation> _calls = new();

        public IReadOnlyCollection<ThreadObservation> Calls => _calls.ToArray();

        public Task<ShellSyncCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(ThreadObservation.Capture());
            return Task.FromResult(snapshot);
        }
    }

    // 第一次读取挂起到测试放行为止，其后的读取立即返回，用来制造"先发起、后返回"的乱序刷新。
    private sealed class FirstCallGatedSyncCenterService(
        ShellSyncCenterSnapshot firstSnapshot,
        ShellSyncCenterSnapshot laterSnapshot) : IShellSyncCenterService
    {
        private readonly TaskCompletionSource<ShellSyncCenterSnapshot> _firstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ShellSyncCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Interlocked.Increment(ref _callCount) == 1
                ? _firstCall.Task
                : Task.FromResult(laterSnapshot);
        }

        public void ReleaseFirstCall() => _firstCall.TrySetResult(firstSnapshot);
    }

    private sealed class StaticSettlementQueueReader(
        LinklySettlementUploadOverview overview,
        IReadOnlyList<LinklySettlementUploadQueueItem> items) : ILinklySettlementUploadQueueReader
    {
        private readonly ConcurrentQueue<ThreadObservation> _calls = new();

        public List<int> RequestedTakes { get; } = [];

        public IReadOnlyCollection<ThreadObservation> Calls => _calls.ToArray();

        public Task<LinklySettlementUploadOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(ThreadObservation.Capture());
            return Task.FromResult(overview);
        }

        public Task<IReadOnlyList<LinklySettlementUploadQueueItem>> GetActiveItemsAsync(
            int take = 20,
            CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(ThreadObservation.Capture());
            RequestedTakes.Add(take);
            return Task.FromResult<IReadOnlyList<LinklySettlementUploadQueueItem>>(items.Take(take).ToArray());
        }
    }

    private sealed class ThrowingSettlementQueueReader(string message) : ILinklySettlementUploadQueueReader
    {
        public Task<LinklySettlementUploadOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<LinklySettlementUploadOverview>(new IOException(message));

        public Task<IReadOnlyList<LinklySettlementUploadQueueItem>> GetActiveItemsAsync(
            int take = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<LinklySettlementUploadQueueItem>>(new IOException(message));
    }

    private sealed class CapturingOrderExecutor(OrderUploadExecutionResult result) : IOrderUploadExecutionService
    {
        public IReadOnlyList<Guid> OneIds { get; private set; } = [];

        public IReadOnlyList<Guid> SelectedIds { get; private set; } = [];

        private readonly ConcurrentQueue<ThreadObservation> _pendingCalls = new();

        public int PendingCallCount => _pendingCalls.Count;

        public IReadOnlyCollection<ThreadObservation> PendingCalls => _pendingCalls.ToArray();

        public int SelectedCallCount { get; private set; }

        public Task<OrderUploadExecutionResult> ExecuteOneAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            OneIds = [.. OneIds, orderGuid];
            return Task.FromResult(result);
        }

        public Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
        {
            _pendingCalls.Enqueue(ThreadObservation.Capture());
            return Task.FromResult(result);
        }

        public Task<OrderUploadExecutionResult> ExecuteSelectedAsync(
            IReadOnlyCollection<Guid> orderGuids,
            CancellationToken cancellationToken = default)
        {
            SelectedCallCount++;
            SelectedIds = orderGuids.ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class CapturingSettlementExecutor(
        LinklySettlementUploadExecutionResult result) : ILinklySettlementUploadExecutionService
    {
        private readonly ConcurrentQueue<ThreadObservation> _pendingCalls = new();

        public IReadOnlyList<Guid> OneIds { get; private set; } = [];

        public int PendingCallCount => _pendingCalls.Count;

        public IReadOnlyCollection<ThreadObservation> PendingCalls => _pendingCalls.ToArray();

        public Task<LinklySettlementUploadExecutionResult> ExecutePendingAsync(
            int batchSize = 20,
            CancellationToken cancellationToken = default)
        {
            _pendingCalls.Enqueue(ThreadObservation.Capture());
            return Task.FromResult(result);
        }

        public Task<LinklySettlementUploadExecutionResult> ExecuteOneAsync(
            Guid settlementGuid,
            CancellationToken cancellationToken = default)
        {
            OneIds = [.. OneIds, settlementGuid];
            return Task.FromResult(result);
        }
    }
}
