using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ReceiptReturnsWorkflowServiceTests
{
    [Fact]
    public async Task LookupOrderAsync_UsesRemoteReturnContextAndCalculatesAvailableQuantity()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var remote = new FakeRemoteOrderHistoryService
        {
            QueryResult = new RemoteOrderHistoryResult(
            [
                new RemoteOrderHistorySummary(orderGuid, "S001", "POS-01", "Alice", DateTimeOffset.UtcNow, 20m, 0m, 20m, 1, "Cash", "Completed")
            ]),
            ReturnContext = new OrderReturnContextDto(
                CreateRemoteOrder(orderGuid, lineGuid, quantity: 2m, actualAmount: 20m),
                [
                    new OrderReturnRecordDto(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        orderGuid,
                        lineGuid,
                        "SKU-001",
                        "REF-001",
                        1m,
                        10m,
                        "C01",
                        DateTimeOffset.UtcNow)
                ])
        };
        var service = CreateService(remote);

        var result = await service.LookupOrderAsync(CreateOnlineSession(), orderGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.True(result.IsRemote);
        Assert.False(result.ReturnRecordsMayBeStale);
        var line = Assert.Single(result.Order.Lines);
        Assert.Equal(1m, line.ReturnedQuantity);
        Assert.Equal(1m, line.AvailableQuantity);
        Assert.Equal(10m, line.ReturnUnitAmount);
        Assert.Equal(0, remote.QueryCallCount);
        Assert.Equal(1, remote.ReturnContextCallCount);
        Assert.True(remote.LastReturnContextCancellationToken.CanBeCanceled);
    }

    [Fact]
    public void RemoteLookupTimeout_IsTwoSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ReceiptReturnsWorkflowService.RemoteLookupTimeout);
    }

    [Fact]
    public async Task LookupOrderAsync_CancelsSlowRemoteAtDedicatedDeadline()
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new FakeRemoteOrderHistoryService
        {
            ReturnContextAsync = async (_, cancellationToken) =>
            {
                // 在取消异常里记录，而不是另注册回调：Cancel() 先执行 Task.Delay 的回调，其续延若被空闲线程
                // 立刻执行，using 会在观察回调运行前注销它，导致取消已发生却永远观察不到。
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }

                return null;
            }
        };
        var service = CreateService(remote);

        // 外层只负责检测真正挂死；共享 Windows CI 高负载下定时器调度可能明显晚于业务的 2 秒 deadline。
        var result = await service
            .LookupOrderAsync(CreateOnlineSession(), Guid.NewGuid().ToString("D"))
            .WaitAsync(TimeSpan.FromSeconds(30));

        await cancellationObserved.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.Null(result.Order);
        Assert.Equal("Online order lookup timed out. Please retry.", result.StatusMessage);
    }

    [Fact]
    public async Task LookupOrderAsync_HoldsUiPriorityUntilRemoteLookupCompletes()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookup = new TaskCompletionSource<OrderReturnContextDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new FakeRemoteOrderHistoryService
        {
            ReturnContextAsync = (_, _) =>
            {
                lookupStarted.TrySetResult();
                // 本测试用闸门单独验证 UI 优先级的持有范围，避免与 2 秒业务 deadline 竞争。
                // 超时取消由 CancelsSlowRemoteAtDedicatedDeadline 测试独立覆盖。
                return releaseLookup.Task;
            }
        };
        var uiPriority = new UiPriorityCoordinator(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));
        var service = CreateService(remote, uiPriorityCoordinator: uiPriority);

        var lookupTask = service.LookupOrderAsync(CreateOnlineSession(), orderGuid.ToString("D"));
        await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(uiPriority.IsUiActive);
        var waitForIdleTask = uiPriority.WaitForUiIdleAsync();
        Assert.False(waitForIdleTask.IsCompleted);

        releaseLookup.SetResult(new OrderReturnContextDto(
            CreateRemoteOrder(orderGuid, lineGuid, quantity: 1m, actualAmount: 10m),
            []));
        var result = await lookupTask.WaitAsync(TimeSpan.FromSeconds(30));
        await waitForIdleTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(result.Order);
        Assert.False(uiPriority.IsUiActive);
    }

    [Fact]
    public async Task LookupOrderAsync_FallsBackToLocalOrderWhenRemoteFails()
    {
        var order = CreateLocalOrder(Guid.NewGuid());
        var localRepository = new FakeLocalOrderRepository([order]);
        var service = CreateService(new ThrowingRemoteOrderHistoryService(), localRepository);

        var result = await service.LookupOrderAsync(CreateOnlineSession(), order.OrderGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.False(result.IsRemote);
        Assert.True(result.ReturnRecordsMayBeStale);
        Assert.Equal(order.OrderGuid, result.Order.OrderGuid);
    }

    [Fact]
    public async Task LookupOrderAsync_RemoteTimeoutReturnsVisibleFailureWithoutLocalFallback()
    {
        var order = CreateLocalOrder(Guid.NewGuid());
        var remote = new FakeRemoteOrderHistoryService
        {
            ReturnContextException = new TaskCanceledException("The request timed out.")
        };
        var service = CreateService(
            remote,
            new FakeLocalOrderRepository([order]));

        var result = await service.LookupOrderAsync(
            CreateOnlineSession(),
            order.OrderGuid.ToString("D"));

        Assert.Null(result.Order);
        Assert.False(result.IsRemote);
        Assert.False(result.ReturnRecordsMayBeStale);
        Assert.Equal("Online order lookup timed out. Please retry.", result.StatusMessage);
        Assert.Equal(1, remote.ReturnContextCallCount);
    }

    [Fact]
    public async Task LookupOrderAsync_ExplicitCancellationStillPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var remote = new FakeRemoteOrderHistoryService
        {
            ReturnContextException = new OperationCanceledException(cancellation.Token)
        };
        var service = CreateService(remote);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.LookupOrderAsync(
                CreateOnlineSession(),
                Guid.NewGuid().ToString("D"),
                cancellation.Token));
    }

    [Theory]
    [InlineData("en-US", "Loaded local order; online lookup failed. Remote unavailable.")]
    [InlineData("zh-CN", "已加载本地订单；线上查询失败。Remote unavailable.")]
    public async Task LookupOrderAsync_RemoteFailureWithLocalOrderFormatsLocalizedStatus(string cultureName, string expectedStatus)
    {
        var order = CreateLocalOrder(Guid.NewGuid());
        var localRepository = new FakeLocalOrderRepository([order]);
        var service = CreateService(
            new ThrowingRemoteOrderHistoryService(),
            localRepository,
            localization: CreateLocalization(cultureName));

        var result = await service.LookupOrderAsync(CreateOnlineSession(), order.OrderGuid.ToString("D"));

        Assert.Equal(expectedStatus, result.StatusMessage);
        Assert.DoesNotContain("{0}", result.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", result.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US", "Loaded online order and return records.")]
    [InlineData("zh-CN", "已从线上订单历史加载订单和退货记录。")]
    public async Task LookupOrderAsync_RemoteOrderStatusDoesNotExposePlaceholder(string cultureName, string expectedStatus)
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var remote = new FakeRemoteOrderHistoryService
        {
            ReturnContext = new OrderReturnContextDto(
                CreateRemoteOrder(orderGuid, lineGuid, quantity: 1m, actualAmount: 10m),
                [])
        };
        var service = CreateService(remote, localization: CreateLocalization(cultureName));

        var result = await service.LookupOrderAsync(CreateOnlineSession(), orderGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.Equal(expectedStatus, result.StatusMessage);
        Assert.DoesNotContain("{0}", result.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US", "Order was not found.")]
    [InlineData("zh-CN", "未找到订单。")]
    public async Task LookupOrderAsync_MissingOrderStatusDoesNotExposePlaceholder(string cultureName, string expectedStatus)
    {
        var service = CreateService(localization: CreateLocalization(cultureName));

        var result = await service.LookupOrderAsync(CreateOnlineSession(), Guid.NewGuid().ToString("D"));

        Assert.Null(result.Order);
        Assert.Equal(expectedStatus, result.StatusMessage);
        Assert.DoesNotContain("{0}", result.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US", "Loaded local order; online order history is not available.")]
    [InlineData("zh-CN", "已加载本地订单；线上订单历史不可用。")]
    public async Task LookupOrderAsync_LocalOrderStatusDoesNotExposePlaceholder(string cultureName, string expectedStatus)
    {
        var order = CreateLocalOrder(Guid.NewGuid());
        var service = CreateService(
            localRepository: new FakeLocalOrderRepository([order]),
            localization: CreateLocalization(cultureName));

        var result = await service.LookupOrderAsync(CreateOfflineSession(), order.OrderGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.Equal(expectedStatus, result.StatusMessage);
        Assert.DoesNotContain("{0}", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupOrderAsync_LocalCardPaymentCapacitiesCarryOriginalOrderGuid()
    {
        var orderGuid = Guid.NewGuid();
        var order = new LocalOrder(
            orderGuid,
            "S001",
            "POS-01",
            "C01",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            [
                new LocalOrderLine(Guid.NewGuid(), "SKU-001", "REF-001", "Milk", "690001", "ITEM-001", 1m, 10m, 0m, 10m, PriceSourceKind.StoreRetailPrice)
            ],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Card, 10m, "SQ:local-payment-1")]);
        var service = CreateService(localRepository: new FakeLocalOrderRepository([order]));

        var result = await service.LookupOrderAsync(CreateOnlineSession(), orderGuid.ToString("D"));

        Assert.NotNull(result.Order);
        var capacity = Assert.Single(result.Order.PaymentCapacities);
        Assert.Equal(PaymentMethodKind.Card, capacity.Method);
        Assert.Equal("SQ:local-payment-1", capacity.Reference);
        Assert.Equal(orderGuid, capacity.OriginalOrderGuid);
    }

    [Fact]
    public async Task LookupOrderAsync_LocalFallbackCountsLocalReturnsAndDisablesOriginalCardRefund()
    {
        var originalGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var original = CreateCardSaleOrder(originalGuid, lineGuid, quantity: 3m, cardReference: "SQ:card-1");
        var pendingReturn = CreateReturnOrder(originalGuid, lineGuid, quantity: 1m, cardRefund: 10m, originalReference: "SQ:card-1");
        var syncedReturn = CreateReturnOrder(originalGuid, lineGuid, quantity: 1m, cardRefund: 10m, originalReference: "SQ:card-1");
        var repository = new FakeLocalOrderRepository([original, pendingReturn, syncedReturn]);
        repository.SyncedOrders.Add(syncedReturn.OrderGuid);
        var service = CreateService(new ThrowingRemoteOrderHistoryService(), repository);

        var result = await service.LookupOrderAsync(CreateOnlineSession(), originalGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.True(result.ReturnRecordsMayBeStale);
        // 降级到本地时服务端记录不可见，本机保存的退货单（含已同步的）都要计入已退数量。
        var line = Assert.Single(result.Order.Lines);
        Assert.Equal(2m, line.ReturnedQuantity);
        Assert.Equal(1m, line.AvailableQuantity);
        Assert.Equal(2, result.Order.ReturnRecords.Count);
        // 陈旧状态下禁止原路退卡：卡付款可退额度清零，不再进入购物车的退款额度。
        var capacity = Assert.Single(result.Order.PaymentCapacities, c => c.Method == PaymentMethodKind.Card);
        Assert.Equal(0m, capacity.RemainingAmount);
        Assert.Equal(capacity.OriginalAmount, capacity.RefundedAmount);
        Assert.Contains("Refund to the original card is disabled", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupOrderAsync_OfflineLocalLookupDisablesOriginalCardRefundWithLocalizedNotice()
    {
        var originalGuid = Guid.NewGuid();
        var original = CreateCardSaleOrder(originalGuid, Guid.NewGuid(), quantity: 1m, cardReference: "SQ:card-1");
        var service = CreateService(
            localRepository: new FakeLocalOrderRepository([original]),
            localization: CreateLocalization("zh-CN"));

        var result = await service.LookupOrderAsync(CreateOfflineSession(), originalGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.All(
            result.Order.PaymentCapacities.Where(c => c.Method == PaymentMethodKind.Card),
            capacity => Assert.Equal(0m, capacity.RemainingAmount));
        Assert.Contains("不能原路退回银行卡", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupOrderAsync_RemoteContextMergesUnsyncedLocalReturnsIntoQuantityAndCardCapacity()
    {
        var originalGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var serverReturnOrderGuid = Guid.NewGuid();
        var pendingReturn = CreateReturnOrder(originalGuid, lineGuid, quantity: 1m, cardRefund: 10m, originalReference: "SQ:card-1");
        var syncedReturn = CreateReturnOrder(originalGuid, lineGuid, quantity: 1m, cardRefund: 10m, originalReference: "SQ:card-1");
        // 服务端已记录、但本机状态尚未回写为 Synced 的退货单：以服务端为准，不能重复计数。
        var alreadyOnServer = CreateReturnOrder(originalGuid, lineGuid, quantity: 1m, cardRefund: 10m, originalReference: "SQ:card-1") with
        {
            OrderGuid = serverReturnOrderGuid
        };
        var repository = new FakeLocalOrderRepository([pendingReturn, syncedReturn, alreadyOnServer]);
        repository.SyncedOrders.Add(syncedReturn.OrderGuid);
        var remote = new FakeRemoteOrderHistoryService
        {
            ReturnContext = new OrderReturnContextDto(
                CreateRemoteOrder(originalGuid, lineGuid, quantity: 5m, actualAmount: 50m),
                [
                    new OrderReturnRecordDto(Guid.NewGuid(), serverReturnOrderGuid, originalGuid, lineGuid, "SKU-001", "REF-001", 1m, 10m, "C01", DateTimeOffset.UtcNow),
                    new OrderReturnRecordDto(Guid.NewGuid(), syncedReturn.OrderGuid, originalGuid, lineGuid, "SKU-001", "REF-001", 1m, 10m, "C01", DateTimeOffset.UtcNow)
                ],
                PaymentCapacities:
                [
                    new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 50m, 20m, 30m, "SQ:card-1", null, originalGuid)
                ])
        };
        var service = CreateService(remote, repository);

        var result = await service.LookupOrderAsync(CreateOnlineSession(), originalGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.True(result.IsRemote);
        Assert.False(result.ReturnRecordsMayBeStale);
        var line = Assert.Single(result.Order.Lines);
        // 服务端 2 件 + 本机未同步的 1 件。
        Assert.Equal(3m, line.ReturnedQuantity);
        Assert.Equal(3, result.Order.ReturnRecords.Count);
        var capacity = Assert.Single(result.Order.PaymentCapacities);
        Assert.Equal(20m, capacity.RemainingAmount);
        Assert.Equal(30m, capacity.RefundedAmount);
    }

    [Fact]
    public async Task LookupOrderAsync_RemoteContextWithUnreadableLocalReturnsFailsClosedOnCardRefund()
    {
        var originalGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var repository = new FakeLocalOrderRepository([]) { ReturnOrdersException = new InvalidOperationException("sqlite busy") };
        var remote = new FakeRemoteOrderHistoryService
        {
            ReturnContext = new OrderReturnContextDto(
                CreateRemoteOrder(originalGuid, lineGuid, quantity: 2m, actualAmount: 20m),
                [],
                PaymentCapacities:
                [
                    new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 20m, 0m, 20m, "SQ:card-1", null, originalGuid)
                ])
        };
        var service = CreateService(remote, repository);

        var result = await service.LookupOrderAsync(CreateOnlineSession(), originalGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.True(result.ReturnRecordsMayBeStale);
        Assert.Equal(0m, Assert.Single(result.Order.PaymentCapacities).RemainingAmount);
    }

    [Fact]
    public async Task LookupOrderAsync_UnmatchedLocalCardRefundBlocksEveryCardPaymentWhenOriginalIsAmbiguous()
    {
        var originalGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        // 退款引用没有 original 部分，且原单有两笔卡付款：无法归属，全部占满。
        var returnOrder = CreateReturnOrder(originalGuid, lineGuid, quantity: 1m, cardRefund: 5m, originalReference: null);
        var repository = new FakeLocalOrderRepository([returnOrder]);
        var remote = new FakeRemoteOrderHistoryService
        {
            ReturnContext = new OrderReturnContextDto(
                CreateRemoteOrder(originalGuid, lineGuid, quantity: 4m, actualAmount: 40m),
                [],
                PaymentCapacities:
                [
                    new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 20m, 0m, 20m, "SQ:card-1", null, originalGuid),
                    new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 20m, 0m, 20m, "SQ:card-2", null, originalGuid)
                ])
        };
        var service = CreateService(remote, repository);

        var result = await service.LookupOrderAsync(CreateOnlineSession(), originalGuid.ToString("D"));

        Assert.NotNull(result.Order);
        Assert.All(result.Order.PaymentCapacities, capacity => Assert.Equal(0m, capacity.RemainingAmount));
    }

    [Fact]
    public async Task LookupNoReceiptProductAsync_ReturnsCurrentLocalCatalogItem()
    {
        var priceIndex = new LocalSellableItemIndex();
        priceIndex.ReplaceAll([CreateItem()]);
        var service = CreateService(priceIndex: priceIndex);

        var result = await service.LookupNoReceiptProductAsync(CreateOnlineSession(), "690001");

        Assert.NotNull(result.Item);
        Assert.Equal("SKU-001", result.Item.ProductCode);
    }

    [Fact]
    public async Task LookupNoReceiptProductAsync_searches_after_exact_miss()
    {
        var priceIndex = new LocalSellableItemIndex();
        priceIndex.ReplaceAll([CreateItem("SKU-001", "Green Tea", "690001", 2m)]);
        var service = CreateService(priceIndex: priceIndex);

        var result = await service.LookupNoReceiptProductAsync(CreateOnlineSession(), "tea");

        Assert.NotNull(result.Item);
        Assert.Equal("SKU-001", result.Item.ProductCode);
    }

    [Theory]
    [InlineData("en-US", "Product was not found in the local catalog.")]
    [InlineData("zh-CN", "未在本地目录中找到商品。")]
    public void LookupNoReceiptProduct_MissingProductStatusDoesNotExposePlaceholder(string cultureName, string expectedStatus)
    {
        var service = CreateService(localization: CreateLocalization(cultureName));

        var result = service.LookupNoReceiptProduct(CreateOnlineSession(), "MISSING-SKU");

        Assert.Null(result.Item);
        Assert.Equal(expectedStatus, result.StatusMessage);
        Assert.DoesNotContain("{0}", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateNoReceiptOpenItem_UsesOpenItemWithEnteredNameAndPrice()
    {
        var priceIndex = new LocalSellableItemIndex();
        priceIndex.ReplaceAll([CreateItem("OPEN-SKU", "Open Item", "OPENITEM", 0m)]);
        var service = CreateService(priceIndex: priceIndex);

        var first = service.CreateNoReceiptOpenItem(CreateOnlineSession(), "Manual Refund", 12.34m);
        var second = service.CreateNoReceiptOpenItem(CreateOnlineSession(), "Manual Refund", 12.34m);

        Assert.NotNull(first.Line);
        Assert.NotNull(second.Line);
        Assert.Equal("OPEN-SKU", first.Line.ProductCode);
        Assert.Equal("Manual Refund", first.Line.DisplayName);
        Assert.Equal("OPENITEM", first.Line.LookupCode);
        Assert.Equal(12.34m, first.Line.UnitPrice);
        Assert.Null(first.Line.OriginalOrderGuid);
        Assert.Null(first.Line.OriginalOrderLineGuid);
        Assert.NotEqual(first.Line.ReturnSourceKey, second.Line.ReturnSourceKey);
    }

    [Fact]
    public void CreateNoReceiptOpenItem_ReturnsErrorWhenOpenItemIsMissingOrDuplicated()
    {
        var missingService = CreateService(priceIndex: new LocalSellableItemIndex());

        var missing = missingService.CreateNoReceiptOpenItem(CreateOnlineSession(), "Manual Refund", 12.34m);

        Assert.Null(missing.Line);
        Assert.Equal("OPENITEM was not found in the local catalog.", missing.StatusMessage);

        var duplicateIndex = new LocalSellableItemIndex();
        duplicateIndex.ReplaceAll([
            CreateItem("OPEN-SKU-1", "Open Item 1", "OPENITEM", 0m),
            CreateItem("OPEN-SKU-2", "Open Item 2", "OPENITEM", 0m)
        ]);
        var duplicateService = CreateService(priceIndex: duplicateIndex);

        var duplicate = duplicateService.CreateNoReceiptOpenItem(CreateOnlineSession(), "Manual Refund", 12.34m);

        Assert.Null(duplicate.Line);
        Assert.Equal("Multiple OPENITEM records were found in the local catalog.", duplicate.StatusMessage);
    }

    private static ReceiptReturnsWorkflowService CreateService(
        IRemoteOrderHistoryService? remote = null,
        FakeLocalOrderRepository? localRepository = null,
        LocalSellableItemIndex? priceIndex = null,
        ILocalizationService? localization = null,
        IUiPriorityCoordinator? uiPriorityCoordinator = null)
    {
        localRepository ??= new FakeLocalOrderRepository([]);
        return new ReceiptReturnsWorkflowService(
            new FakeReceiptQueryService(localRepository),
            localRepository,
            remote,
            priceIndex ?? new LocalSellableItemIndex(),
            new PosCartService(),
            localization,
            uiPriorityCoordinator);
    }

    private static PosSessionState CreateOnlineSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C01", "Alice", true, 0);
    }

    private static PosSessionState CreateOfflineSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C01", "Alice", false, 0);
    }

    private static LocalizationService CreateLocalization(string cultureName)
    {
        var localization = new LocalizationService();
        localization.SetCulture(cultureName);
        return localization;
    }

    private static OrderHistoryDetailsDto CreateRemoteOrder(Guid orderGuid, Guid lineGuid, decimal quantity, decimal actualAmount)
    {
        return new OrderHistoryDetailsDto(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            actualAmount,
            0m,
            actualAmount,
            [
                new OrderHistoryLineDto(lineGuid, "SKU-001", "REF-001", "Milk", "690001", "ITEM-001", quantity, 10m, 0m, actualAmount)
            ],
            [new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, actualAmount, null)]);
    }

    private static LocalOrder CreateCardSaleOrder(Guid orderGuid, Guid lineGuid, decimal quantity, string cardReference)
    {
        var amount = quantity * 10m;
        return new LocalOrder(
            orderGuid,
            "S001",
            "POS-01",
            "C01",
            "Alice",
            DateTimeOffset.UtcNow,
            amount,
            0m,
            amount,
            [
                new LocalOrderLine(lineGuid, "SKU-001", "REF-001", "Milk", "690001", "ITEM-001", quantity, 10m, 0m, amount, PriceSourceKind.StoreRetailPrice)
            ],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Card, amount, cardReference)]);
    }

    private static LocalOrder CreateReturnOrder(
        Guid originalOrderGuid,
        Guid originalLineGuid,
        decimal quantity,
        decimal cardRefund,
        string? originalReference)
    {
        var refundReference = originalReference is null
            ? "SQ:refund-1"
            : CardRefundReference.Format("SQ:refund-1", originalReference);
        return new LocalOrder(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            "C01",
            "Alice",
            DateTimeOffset.UtcNow,
            -quantity * 10m,
            0m,
            -quantity * 10m,
            [
                new LocalOrderLine(
                    Guid.NewGuid(),
                    "SKU-001",
                    "REF-001",
                    "Milk",
                    "690001",
                    "ITEM-001",
                    quantity,
                    10m,
                    0m,
                    -quantity * 10m,
                    PriceSourceKind.StoreRetailPrice,
                    OrderLineKind.Return,
                    $"RETURN:{Guid.NewGuid():N}",
                    originalOrderGuid,
                    originalLineGuid)
            ],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Card, -cardRefund, refundReference)]);
    }

    private static LocalOrder CreateLocalOrder(Guid orderGuid)
    {
        return new LocalOrder(
            orderGuid,
            "S001",
            "POS-01",
            "C01",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            [
                new LocalOrderLine(Guid.NewGuid(), "SKU-001", "REF-001", "Milk", "690001", "ITEM-001", 1m, 10m, 0m, 10m, PriceSourceKind.StoreRetailPrice)
            ],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, 10m, null)]);
    }

    private static SellableItemDto CreateItem(
        string productCode = "SKU-001",
        string displayName = "Milk",
        string lookupCode = "690001",
        decimal retailPrice = 10m)
    {
        return new SellableItemDto(
            "S001",
            productCode,
            "REF-001",
            displayName,
            lookupCode,
            lookupCode,
            lookupCode,
            retailPrice,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            1m,
            DateTimeOffset.UtcNow);
    }

    private sealed class FakeReceiptQueryService(FakeLocalOrderRepository repository) : IReceiptQueryService
    {
        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return repository.GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return repository.GetRecentOrdersAsync(query, take, cancellationToken);
        }

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }
    }

    private sealed class FakeLocalOrderRepository(IEnumerable<LocalOrder> orders) : ILocalOrderRepository
    {
        private readonly Dictionary<Guid, LocalOrder> _orders = orders.ToDictionary(order => order.OrderGuid);

        public HashSet<Guid> SyncedOrders { get; } = [];

        public Exception? ReturnOrdersException { get; init; }

        public Task<IReadOnlyList<LocalOrder>> GetReturnOrdersForOriginalAsync(
            Guid originalOrderGuid,
            bool unsyncedOnly,
            CancellationToken cancellationToken = default)
        {
            if (ReturnOrdersException is not null)
            {
                return Task.FromException<IReadOnlyList<LocalOrder>>(ReturnOrdersException);
            }

            return Task.FromResult<IReadOnlyList<LocalOrder>>(_orders.Values
                .Where(order => order.Lines.Any(line =>
                    line.Kind == OrderLineKind.Return && line.OriginalOrderGuid == originalOrderGuid))
                .Where(order => !unsyncedOnly || !SyncedOrders.Contains(order.OrderGuid))
                .ToList());
        }

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            _orders[order.OrderGuid] = order;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(new LocalOrderHistoryQuery(), take, cancellationToken);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>(_orders.Values
                .OrderByDescending(order => order.SoldAt)
                .Take(take)
                .Select(order => new LocalOrderSummary(order.OrderGuid, order.StoreCode, order.DeviceCode, order.CashierName, order.SoldAt, order.TotalAmount, order.DiscountAmount, order.ActualAmount, "Pending", order.Lines.Count, "Cash"))
                .ToList());
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_orders.TryGetValue(orderGuid, out var order) ? order : null);
        }
    }

    private sealed class FakeRemoteOrderHistoryService : IRemoteOrderHistoryService
    {
        public RemoteOrderHistoryResult QueryResult { get; init; } = new([]);

        public OrderReturnContextDto? ReturnContext { get; init; }

        public Exception? ReturnContextException { get; init; }

        public Func<Guid, CancellationToken, Task<OrderReturnContextDto?>>? ReturnContextAsync { get; init; }

        public int QueryCallCount { get; private set; }

        public int ReturnContextCallCount { get; private set; }

        public CancellationToken LastReturnContextCancellationToken { get; private set; }

        public Task<RemoteOrderHistoryResult> QueryAsync(RemoteOrderHistoryQuery query, CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            return Task.FromResult(QueryResult);
        }

        public Task<ReceiptDetails?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            ReturnContextCallCount++;
            LastReturnContextCancellationToken = cancellationToken;
            if (ReturnContextException is not null)
            {
                return Task.FromException<OrderReturnContextDto?>(ReturnContextException);
            }

            if (ReturnContextAsync is not null)
            {
                return ReturnContextAsync(orderGuid, cancellationToken);
            }

            return Task.FromResult(ReturnContext);
        }

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(OrderReturnRecordCreateRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OrderReturnRecordCreateResponse(request.ReturnOrderGuid, []));
        }
    }

    private sealed class ThrowingRemoteOrderHistoryService : IRemoteOrderHistoryService
    {
        public Task<RemoteOrderHistoryResult> QueryAsync(RemoteOrderHistoryQuery query, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Remote unavailable.");
        }

        public Task<ReceiptDetails?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Remote unavailable.");
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Remote unavailable.");
        }

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(OrderReturnRecordCreateRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Remote unavailable.");
        }
    }
}
